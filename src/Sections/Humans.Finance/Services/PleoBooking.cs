using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using Humans.Pleo.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Books Pleo spend into Holded treasury 57200003, one journal entry per Pleo entry, gross. Reads Pleo through
/// <see cref="IPleoService"/> and the ledger through <see cref="IHoldedService"/>, posts through
/// <see cref="IHoldedWrite"/>. Owns no tables and stores no Holded ids: an entry is in Holded when a 57200003 line's
/// description starts <c>Pleo {Id}:</c>.
/// </summary>
/// <remarks>
/// Duplicates are kept rare, not made impossible: one run at a time (a process-wide gate, one server), and every run
/// re-pulls 57200003 from Holded and skips what it shows. A post Holded took but does not list yet can be posted
/// again; the page flags any Pleo id on two entries so the extra one is deleted in Holded.
/// </remarks>
internal sealed class PleoBooking(
    IPleoService pleo,
    IHoldedService holded,
    IHoldedWrite write,
    IAuditLogService audit,
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
        IReadOnlySet<int> openAccounts)
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var entriesById = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var topUpLines = new List<HoldedLedgerLineInfo>();
        var legacy = new List<HoldedLedgerLineInfo>();
        foreach (var l in pleoLines)
        {
            if (PleoIdOf(l) is { } id)
            {
                byId.TryAdd(id, l.EntryNumber);
                if (!entriesById.TryGetValue(id, out var set)) entriesById[id] = set = [];
                set.Add(l.EntryNumber);
            }
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
                if (e.AmountEur is { } amt && amt != 0m
                    && Take(legacy, date, l => amt < 0m ? l.Credit == -amt : l.Debit == amt) is { } old)
                    return new PleoSpendRow(e, date, PleoSpendState.Booked, account, old.EntryNumber);
                return new PleoSpendRow(e, date, Readiness(e, openAccounts), account, null);
            })
            .ToList();

        var duplicates = entriesById.Where(kv => kv.Value.Count > 1)
            .Select(kv => new PleoDuplicate(kv.Key, kv.Value.ToList()))
            .ToList();
        return new PleoLedgerView(topUps, spend, duplicates);
    }

    public async Task<PleoReconcileVm> GetReconcileAsync(CancellationToken ct = default)
    {
        var (view, chart) = await LoadAsync(ct);
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
            state.Run);
    }

    private async Task<(PleoLedgerView View, IReadOnlyList<HoldedChartAccountInfo> Chart)> LoadAsync(CancellationToken ct)
    {
        var today = MadridDate(clock.GetCurrentInstant());
        var entries = await pleo.GetSettledEntriesAsync(Inception, today, ct);
        var pleoLines = await holded.GetLedgerLinesAsync(PleoTreasury, ct);
        var sabadell = (await holded.GetLedgerLinesAsync(SabadellTreasury, ct)).Select(l => l.EntryNumber).ToHashSet();
        var chart = await holded.GetChartAsync(ct);
        var open = chart.Where(a => !a.Archived).Select(a => a.Number).ToHashSet();
        var view = Classify(entries, pleoLines, sabadell, open);
        return (view, chart);
    }

    /// <summary>Books the oldest <paramref name="limit"/> ready entries (all when null). Refused, nothing posted, while
    /// another run holds the gate, Holded is not configured, or a ledger sweep is running. A job may call this as the button does.</summary>
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

            var (view, _) = await LoadAsync(ct);

            var booked = view.Spend.Where(r => r.State == PleoSpendState.Booked)
                .Select(r => r.Entry.Id).ToHashSet(StringComparer.Ordinal);
            var batch = view.Spend.Where(r => r.State == PleoSpendState.Ready).Take(limit ?? int.MaxValue).ToList();
            run = run with { Total = batch.Count };
            state.Publish(run);

            foreach (var row in batch)
            {
                var e = row.Entry;
                if (booked.Contains(e.Id))
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
                    lines.Add(Line(row, PleoBookingOutcome.Failed, "Holded refused it: " + ex.Message));
                    state.Publish(run with { Lines = lines.ToList() });
                    continue;
                }
                catch (HoldedTransientException ex)
                {
                    logger.LogWarning("Holded did not answer the post of Pleo entry {PleoId}; it may have posted: {Reason}",
                        e.Id, ex.Message);
                    // It may have landed; the run stops while Holded is struggling.
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

    /// <summary>Re-pulls 57200003 once more, so each booked line names its Holded entry.</summary>
    private async Task ConfirmInLedgerAsync(List<PleoBookingLine> lines)
    {
        if (!lines.Any(l => l.Outcome is PleoBookingOutcome.Posted or PleoBookingOutcome.Unconfirmed)) return;
        try
        {
            if (!await holded.SyncAccountLedgerAsync(PleoTreasury, CancellationToken.None)) return;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var l in await holded.GetLedgerLinesAsync(PleoTreasury, CancellationToken.None))
                if (PleoIdOf(l) is { } id) seen.TryAdd(id, l.EntryNumber);
            for (var i = 0; i < lines.Count; i++)
                if (seen.TryGetValue(lines[i].PleoId, out var entry))
                    lines[i] = lines[i] with { HoldedEntry = entry };
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Could not re-read 57200003 after a Pleo booking run");
        }
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
