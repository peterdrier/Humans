using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using Humans.Pleo.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Books Pleo spend into Holded treasury 57200003, one journal entry per Pleo entry, gross. Reads Pleo through
/// <see cref="IPleoService"/> and the ledger through <see cref="IHoldedService"/>, posts through
/// <see cref="IHoldedWrite"/>. Stores no Holded ids: an entry is in Holded when a 57200003 line's description starts
/// <c>Pleo {Id}:</c>. Its one table, <c>pleo_booking_holds</c>, guards posts the ledger does not show yet.
/// </summary>
/// <remarks>
/// Never posting twice: every run takes a process-wide gate (one server), re-pulls 57200003's whole history from
/// Holded, and judges from that; each entry is re-checked against it and against this run's own posts right before
/// its post. Before each post a hold row is inserted (the primary key makes it a claim: a second insert fails and
/// nothing posts); it is deleted only when a re-pull shows the entry, Holded refused it, or a human released it after
/// checking Holded. A run is refused while any hold stands, so Holded's read-after-write lag, an unconfirmed post, an
/// unanswered one, or a restart mid-post can never post the entry again.
/// </remarks>
internal sealed class PleoBooking(
    IPleoService pleo,
    IHoldedService holded,
    IHoldedWrite write,
    IAuditLogService audit,
    IHoldedRepository repo,
    PleoBookingState state,
    IClock clock,
    ILogger<PleoBooking> logger) : IApplicationService
{
    internal const int PleoTreasury = 57200003;
    internal const int SabadellTreasury = 57200001;

    /// <summary>How far a Holded date may sit from Pleo's settled date and still match a top-up or a legacy line.</summary>
    internal const int MatchDays = 3;

    private const string IdPrefix = "Pleo ";
    private const string AuditEntityType = "PleoEntry";
    private const string JobName = "pleo-booking";

    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly LocalDate Inception = new(2020, 1, 1);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static string Description(PleoEntryInfo e) => $"{IdPrefix}{e.Id}: {e.MerchantName}".TrimEnd();

    /// <summary>The Pleo id a 57200003 line names, or null for a line booked any other way.</summary>
    internal static string? PleoIdOf(HoldedLedgerLineInfo line)
    {
        var d = line.Description;
        if (d is null || !d.StartsWith(IdPrefix, StringComparison.Ordinal)) return null;
        var colon = d.IndexOf(':', IdPrefix.Length);
        return colon > IdPrefix.Length ? d[IdPrefix.Length..colon] : null;
    }

    private static bool IsCompleted(PleoEntryInfo e) => string.Equals(e.Status, "COMPLETED", StringComparison.Ordinal);

    /// <summary>Wallet-moving spend: everything settled but loads and out-of-pocket (which never touched the wallet
    /// and goes through expense claims).</summary>
    internal static bool IsSpend(PleoEntryInfo e) =>
        IsCompleted(e) && e.Family is not ("WALLET" or "OUT_OF_POCKET");

    internal static bool IsTopUp(PleoEntryInfo e) =>
        IsCompleted(e) && string.Equals(e.Family, "WALLET", StringComparison.Ordinal) && e.AmountEur > 0m;

    /// <summary>Spend: Dr the category's account, Cr Pleo. A refund (positive) the other way round.</summary>
    internal static (int Debit, int Credit) Sides(PleoSpendRow row) =>
        row.IsRefund ? (PleoTreasury, row.Account!.Value) : (row.Account!.Value, PleoTreasury);

    /// <summary>Why an entry not yet in Holded can or cannot be booked. A receipt and an open chart account are both
    /// required; nothing is guessed.</summary>
    internal static PleoSpendState Readiness(PleoEntryInfo e, IReadOnlySet<int> openAccounts)
    {
        if (e.AmountEur is null or 0m) return PleoSpendState.NoAmount;
        var noCategory = e.CategoryId is null;
        if (!e.HasReceipt && noCategory) return PleoSpendState.NoReceiptNoCategory;
        if (!e.HasReceipt) return PleoSpendState.NoReceipt;
        if (noCategory) return PleoSpendState.NoCategory;
        return AccountOf(e, openAccounts) is null ? PleoSpendState.NotMapped : PleoSpendState.Ready;
    }

    private static int? AccountOf(PleoEntryInfo e, IReadOnlySet<int> openAccounts) =>
        int.TryParse(e.HoldedCode, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && openAccounts.Contains(n) ? n : null;

    private static LocalDate MadridDate(Instant i) => i.InZone(Madrid).Date;

    /// <summary>
    /// Classifies the mirror against 57200003's lines. A spend entry is booked when a line names its Pleo id; failing
    /// that, a line without one (booked before this, e.g. entry 2061) on the same side, for the same amount, within
    /// <see cref="MatchDays"/> days, counts once for the oldest entry it fits. Top-ups pair with debit lines whose entry
    /// also has a 57200001 leg, the same way. Both lists are oldest first.
    /// </summary>
    internal static PleoLedgerView Classify(
        IReadOnlyList<PleoEntryInfo> entries,
        IReadOnlyList<HoldedLedgerLineInfo> pleoLines,
        IReadOnlySet<int> sabadellEntries,
        IReadOnlySet<int> openAccounts,
        IReadOnlySet<string> pending)
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var topUpLines = new List<HoldedLedgerLineInfo>();
        var legacy = new List<HoldedLedgerLineInfo>();
        foreach (var l in pleoLines)
        {
            if (PleoIdOf(l) is { } id) byId.TryAdd(id, l.EntryNumber);
            else if (l.Debit > 0m && sabadellEntries.Contains(l.EntryNumber)) topUpLines.Add(l);
            else legacy.Add(l);
        }

        static HoldedLedgerLineInfo? Take(List<HoldedLedgerLineInfo> pool, LocalDate date, Func<HoldedLedgerLineInfo, bool> fits)
        {
            var hit = pool
                .Where(l => fits(l) && Math.Abs(Period.Between(date, MadridDate(l.Date), PeriodUnits.Days).Days) <= MatchDays)
                .OrderBy(l => Math.Abs(Period.Between(date, MadridDate(l.Date), PeriodUnits.Days).Days))
                .ThenBy(l => l.EntryNumber)
                .FirstOrDefault();
            if (hit is not null) pool.Remove(hit);
            return hit;
        }

        var topUps = entries.Where(IsTopUp)
            .OrderBy(e => e.SettledAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e =>
            {
                var date = MadridDate(e.SettledAt);
                var amount = e.AmountEur!.Value;
                return new PleoTopUpRow(e.Id, date, amount, Take(topUpLines, date, l => l.Debit == amount)?.EntryNumber);
            })
            .ToList();

        var spend = entries.Where(IsSpend)
            .OrderBy(e => e.SettledAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e =>
            {
                var date = MadridDate(e.SettledAt);
                var account = AccountOf(e, openAccounts);
                if (byId.TryGetValue(e.Id, out var entry))
                    return new PleoSpendRow(e, date, PleoSpendState.Booked, account, entry);
                if (pending.Contains(e.Id))
                    return new PleoSpendRow(e, date, PleoSpendState.Pending, account, null);
                if (e.AmountEur is { } amt && amt != 0m
                    && Take(legacy, date, l => amt < 0m ? l.Credit == -amt : l.Debit == amt) is { } old)
                    return new PleoSpendRow(e, date, PleoSpendState.Booked, account, old.EntryNumber);
                return new PleoSpendRow(e, date, Readiness(e, openAccounts), account, null);
            })
            .ToList();

        return new PleoLedgerView(topUps, spend);
    }

    public async Task<PleoReconcileVm> GetReconcileAsync(CancellationToken ct = default)
    {
        var holds = await repo.GetPleoBookingHoldsAsync(ct);
        var (view, chart) = await LoadAsync(holds, ct);
        var names = chart.ToDictionary(a => a.Number, a => a.EnglishName ?? a.Name);
        var readyByAccount = view.Spend.Where(r => r.State == PleoSpendState.Ready)
            .GroupBy(r => r.Account!.Value)
            .Select(g => new PleoAccountTotalVm(g.Key, names.GetValueOrDefault(g.Key, ""), g.Count(), g.Sum(r => r.Amount)))
            .OrderBy(a => a.AccountNum)
            .ToList();

        return new PleoReconcileVm(
            await pleo.GetWalletBalanceAsync(ct),
            (await holded.GetAccountBalancesAsync(ct)).GetValueOrDefault(PleoTreasury),
            await holded.GetLedgerSyncedAtAsync(ct),
            holded.IsConfigured,
            view,
            readyByAccount,
            holds,
            state.Run);
    }

    private async Task<(PleoLedgerView View, IReadOnlyList<HoldedChartAccountInfo> Chart)> LoadAsync(
        IReadOnlyList<string> holds, CancellationToken ct)
    {
        var today = MadridDate(clock.GetCurrentInstant());
        var entries = await pleo.GetSettledEntriesAsync(Inception, today, ct);
        var pleoLines = await holded.GetLedgerLinesAsync(PleoTreasury, ct);
        var sabadell = (await holded.GetLedgerLinesAsync(SabadellTreasury, ct)).Select(l => l.EntryNumber).ToHashSet();
        var chart = await holded.GetChartAsync(ct);
        var open = chart.Where(a => !a.Archived).Select(a => a.Number).ToHashSet();
        var view = Classify(entries, pleoLines, sabadell, open, holds.ToHashSet(StringComparer.Ordinal));
        return (view, chart);
    }

    /// <summary>Books the oldest <paramref name="limit"/> ready entries (all when null). Refused, nothing posted, while
    /// another run holds the gate, Holded is not configured, a ledger sweep is running, or an earlier post is not in
    /// the ledger yet. A job may call this as the button does.</summary>
    public async Task<PleoBookingRunVm> BookAsync(int? limit, Guid? actorUserId, CancellationToken ct = default)
    {
        var started = clock.GetCurrentInstant();
        if (!await Gate.WaitAsync(0, ct))
            return new PleoBookingRunVm(started, started, limit, 0, [],
                "Another booking run is going; nothing was booked.");
        try
        {
            return await BookUnderGateAsync(limit, actorUserId, started, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<PleoBookingRunVm> BookUnderGateAsync(
        int? limit, Guid? actorUserId, Instant started, CancellationToken ct)
    {
        var run = new PleoBookingRunVm(started, null, limit, 0, [], null);
        state.Publish(run);
        var lines = new List<PleoBookingLine>();

        PleoBookingRunVm Finish(string? refused = null)
        {
            run = run with { FinishedAt = clock.GetCurrentInstant(), Lines = lines.ToList(), Refused = refused };
            state.Publish(run);
            return run;
        }

        try
        {
            if (!holded.IsConfigured) return Finish("Holded is not configured here; nothing was booked.");
            if (!await holded.SyncAccountLedgerAsync(PleoTreasury, ct))
                return Finish("A Holded ledger sync is running; nothing was booked. Try again in a minute.");

            var (view, _) = await LoadAsync(await repo.GetPleoBookingHoldsAsync(ct), ct);
            var inLedger = view.Spend.Where(r => r.State == PleoSpendState.Booked).Select(r => r.Entry.Id).ToList();
            await repo.RemovePleoBookingHoldsAsync(inLedger, ct);
            if (await repo.GetPleoBookingHoldsAsync(ct) is { Count: > 0 } held)
                return Finish($"{held.Count} earlier post(s) are not in Holded's {PleoTreasury} ledger yet "
                              + $"({string.Join(", ", held)}). Check them in Holded before booking more.");

            var booked = view.Spend.Where(r => r.State == PleoSpendState.Booked)
                .Select(r => r.Entry.Id).ToHashSet(StringComparer.Ordinal);
            var batch = view.Spend.Where(r => r.State == PleoSpendState.Ready).Take(limit ?? int.MaxValue).ToList();
            run = run with { Total = batch.Count };
            state.Publish(run);

            foreach (var row in batch)
            {
                var e = row.Entry;
                if (booked.Contains(e.Id) || !await repo.TryAddPleoBookingHoldAsync(e.Id, clock.GetCurrentInstant()))
                {
                    lines.Add(Line(row, PleoBookingOutcome.Skipped, "already in Holded"));
                    continue;
                }

                var (debit, credit) = Sides(row);
                var amount = Math.Abs(row.Amount);
                var stop = false;
                PleoBookingLine line;
                try
                {
                    // Never the request's token: once Holded has the post, abandoning the call only hides it.
                    var outcome = await write.PostJournalEntryAsync(
                        row.Date, debit, credit, amount, Description(e), CancellationToken.None);
                    line = outcome.Confirmed
                        ? Line(row, PleoBookingOutcome.Posted, null)
                        : Line(row, PleoBookingOutcome.Unconfirmed, "Holded took it without an id: check in Holded");
                }
                catch (HoldedPermanentException ex)
                {
                    logger.LogWarning("Holded refused Pleo entry {PleoId} ({Amount} on {Account}): {Reason}",
                        e.Id, amount, row.Account, ex.Message);
                    await repo.RemovePleoBookingHoldsAsync([e.Id], CancellationToken.None);
                    lines.Add(Line(row, PleoBookingOutcome.Failed, "Holded refused it: " + ex.Message));
                    state.Publish(run with { Lines = lines.ToList() });
                    continue;
                }
                catch (HoldedTransientException ex)
                {
                    logger.LogWarning("Holded did not answer the post of Pleo entry {PleoId}; it stays held: {Reason}",
                        e.Id, ex.Message);
                    // It may have landed: held, never retried, and the run stops while Holded is struggling.
                    line = Line(row, PleoBookingOutcome.Failed, $"Holded did not answer ({ex.Message}): it may have posted, check in Holded");
                    stop = true;
                }

                booked.Add(e.Id);
                lines.Add(line);
                await AuditAsync(row, debit, credit, amount, line, actorUserId);
                state.Publish(run with { Lines = lines.ToList() });
                if (stop) return Finish("Stopped: Holded did not answer.");
            }

            await ConfirmInLedgerAsync(lines);
            return Finish();
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Pleo booking could not read Holded");
            return Finish("Holded could not be read: " + ex.Message);
        }
        finally
        {
            if (run.IsRunning) Finish("Booking stopped unexpectedly; see the log.");
        }
    }

    /// <summary>Re-pulls 57200003 once more, so each line names its Holded entry and what shows up is no longer held.
    /// A failure here leaves the holds in place, which is the safe side.</summary>
    private async Task ConfirmInLedgerAsync(List<PleoBookingLine> lines)
    {
        if (!lines.Any(l => l.Outcome is PleoBookingOutcome.Posted or PleoBookingOutcome.Unconfirmed)) return;
        try
        {
            if (!await holded.SyncAccountLedgerAsync(PleoTreasury, CancellationToken.None)) return;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var l in await holded.GetLedgerLinesAsync(PleoTreasury, CancellationToken.None))
                if (PleoIdOf(l) is { } id) seen.TryAdd(id, l.EntryNumber);
            await repo.RemovePleoBookingHoldsAsync(seen.Keys, CancellationToken.None);
            for (var i = 0; i < lines.Count; i++)
                if (seen.TryGetValue(lines[i].PleoId, out var entry))
                    lines[i] = lines[i] with { HoldedEntry = entry };
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Could not re-read 57200003 after a Pleo booking run; its posts stay held");
        }
    }

    public async Task<IReadOnlyList<string>> ClearPendingAsync(Guid actorUserId)
    {
        var cleared = await repo.RemovePleoBookingHoldsAsync(null);
        if (cleared.Count > 0)
            await audit.LogAsync(FinanceAuditActions.PleoBookingHoldCleared, AuditEntityType, Guid.Empty,
                $"Released {cleared.Count} held Pleo post(s) after checking Holded: {string.Join(", ", cleared)}. "
                + "Any not in Holded's ledger can be booked again.", actorUserId);
        return cleared;
    }

    private static PleoBookingLine Line(PleoSpendRow row, PleoBookingOutcome outcome, string? error) =>
        new(row.Entry.Id, row.Date, row.Entry.MerchantName, row.Account!.Value, row.Amount, outcome, null, error);

    private Task AuditAsync(PleoSpendRow row, int debit, int credit, decimal amount, PleoBookingLine line, Guid? actor)
    {
        var text = $"Pleo {row.Entry.Id} ({row.Entry.MerchantName}, {row.Date.ToInvariantDate()}): Dr {debit} / Cr {credit} "
                   + $"{amount.ToEuro()} — {line.Outcome}{(line.Error is null ? "" : ": " + line.Error)}";
        return actor is { } a
            ? audit.LogAsync(FinanceAuditActions.PleoEntryBooked, AuditEntityType, Guid.Empty, text, a)
            : audit.LogAsync(FinanceAuditActions.PleoEntryBooked, AuditEntityType, Guid.Empty, text, JobName);
    }
}
