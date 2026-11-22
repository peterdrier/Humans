using System.Globalization;
using System.Text.RegularExpressions;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using Humans.Pleo.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>What the Holded mirrors and Finance's own rows say, read once per page or run.</summary>
internal sealed record PleoSnapshot(
    IReadOnlyList<PleoEntryInfo> Entries,
    IReadOnlyList<HoldedLedgerLineInfo> PleoLines,
    IReadOnlySet<int> SabadellEntries,
    IReadOnlyList<HoldedChartAccountInfo> Chart,
    IReadOnlyList<HoldedPurchaseDocInfo> Purchases,
    IReadOnlyList<HoldedLedgerLineInfo> ExpenseLines,
    IReadOnlySet<string> Claims,
    IReadOnlyList<PleoReceiptUpload> Uploads);

/// <summary>
/// Books Pleo spend into Holded as purchases on one supplier contact ("Pleo card purchases"), paid from treasury
/// 57200003, with the Pleo receipts attached; attaches receipts that arrive later; and posts a correcting journal entry
/// when a booked entry is recategorised or re-priced in Pleo. Reads Pleo through <see cref="IPleoService"/> and Holded
/// through <see cref="IHoldedService"/>'s mirrors, writes through <see cref="IHoldedWrite"/>.
/// </summary>
/// <remarks>
/// <para>Identity: an entry is in Holded when a purchase is numbered <c>Pleo-{Id}</c>, or a 57200003 line names it
/// (<c>Pleo {Id}:</c> or <c>Pleo-{Id}</c>). Finance stores no Holded id.</para>
/// <para>Never twice: one run at a time (a process-wide gate, one server). Every run refreshes the purchase and
/// 57200003 mirrors first. A purchase is created only after its entry is claimed in <c>finance_pleo_booking_claims</c>
/// (a primary key, so a second claim fails): a claimed entry whose purchase Holded does not list is shown as "check in
/// Holded" and never created again, and one Holded refused outright is released. A purchase that exists but is not
/// approved or paid is finished, never re-created. Receipts are recorded in <c>finance_pleo_receipt_uploads</c> when
/// sent, because Holded's purchase read reports no attachments.</para>
/// </remarks>
internal sealed partial class PleoBooking(
    IOrgTimeZone orgTimeZone,
    IPleoService pleo,
    IHoldedService holded,
    IHoldedWrite write,
    IFinanceRepository repo,
    IAuditLogService audit,
    PleoBookingState state,
    IClock clock,
    ILogger<PleoBooking> logger) : IApplicationService
{
    internal const int PleoTreasury = 57200003;
    internal const int SabadellTreasury = 57200001;
    internal const string NumberPrefix = "Pleo-";

    /// <summary>How far a Holded date may sit from Pleo's settled date and still match a top-up.</summary>
    internal const int MatchDays = 3;

    /// <summary>How long before a refund its original charge is looked for.</summary>
    internal const int RefundLookbackDays = 30;

    /// <summary>Holded's limit on one attachment.</summary>
    internal const int MaxAttachmentBytes = 10 * 1024 * 1024;

    /// <summary><see cref="IHoldedWrite.PayPurchaseDocumentAsync"/>'s ref for a payment Holded took without an id.</summary>
    private const string UnconfirmedRef = "unconfirmed:";

    private const string AuditEntityType = "PleoEntry";
    private const string JobName = "pleo-booking";

    private DateTimeZone OrgZone => orgTimeZone.Zone;
    private static readonly LocalDate Inception = new(2020, 1, 1);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"(?:^|\s)Pleo(?:-(?<id>[^\s:]+)|\s(?<id>[^\s:]+):)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PleoIdPattern();

    internal static string Number(string pleoId) => NumberPrefix + pleoId;

    /// <summary>The Pleo id a ledger line names, as <c>Pleo {Id}:</c> (journal entries) or <c>Pleo-{Id}</c> (a
    /// purchase's own lines and its payment, which Holded writes as <c>Purchase Pleo-{Id} …</c>); null for none.</summary>
    internal static string? PleoIdOf(string? description) =>
        description is not null && PleoIdPattern().Match(description) is { Success: true } m ? m.Groups["id"].Value : null;

    private static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    private static bool IsCompleted(PleoEntryInfo e) => string.Equals(e.Status, "COMPLETED", StringComparison.Ordinal);

    /// <summary>Wallet-moving spend: everything settled but loads and out-of-pocket (which never touched the wallet
    /// and goes through expense claims).</summary>
    internal static bool IsSpend(PleoEntryInfo e) =>
        IsCompleted(e) && e.Family is not ("WALLET" or "OUT_OF_POCKET");

    internal static bool IsTopUp(PleoEntryInfo e) =>
        IsCompleted(e) && string.Equals(e.Family, "WALLET", StringComparison.Ordinal) && e.AmountEur > 0m;

    /// <summary>Why an entry not yet in Holded can or cannot be booked. A missing receipt does not block it.</summary>
    internal static PleoSpendState Readiness(PleoEntryInfo e, IReadOnlySet<int> openAccounts)
    {
        if (e.AmountEur is null or 0m) return PleoSpendState.NoAmount;
        if (e.CategoryId is null) return PleoSpendState.NoCategory;
        return AccountOf(e, openAccounts) is null ? PleoSpendState.NotMapped : PleoSpendState.Ready;
    }

    /// <summary>Only a 629 account: corrections read 629 lines, so a booking anywhere else could drift unseen.</summary>
    private static int? AccountOf(PleoEntryInfo e, IReadOnlySet<int> openAccounts) =>
        int.TryParse(e.HoldedCode, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && IsExpenseAccount(n) && openAccounts.Contains(n) ? n : null;

    private static bool IsExpenseAccount(int n) => n / 100000 == 629;

    private static LocalDate OrgDate(Instant i, DateTimeZone zone) => i.InZone(zone).Date;

    // ── What is written to Holded: plain, Spanish / English ─────────────────────────────────────────────────

    private static string Merchant(PleoEntryInfo e) => string.IsNullOrWhiteSpace(e.MerchantName) ? "—" : e.MerchantName.Trim();

    internal static string LineName(PleoEntryInfo e) =>
        e.AmountEur > 0m ? $"Devolución / refund: {Merchant(e)}"
        : string.Equals(e.Family, "PLEO_INVOICE", StringComparison.Ordinal) ? "Suscripción Pleo / Pleo subscription"
        : Merchant(e);

    internal static string DocDescription(PleoEntryInfo e) =>
        e.AmountEur > 0m ? $"Pleo {e.Id}: devolución con tarjeta Pleo / Pleo card refund, {Merchant(e)}"
        : $"Pleo {e.Id}: pago con tarjeta Pleo / Pleo card payment, {Merchant(e)}";

    internal static string? DocNotes(PleoEntryInfo e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Note)) parts.Add($"Nota / note: {e.Note.Trim()}");
        if (!string.IsNullOrWhiteSpace(e.Cardholder)) parts.Add($"Titular / cardholder: {e.Cardholder.Trim()}");
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    internal static string ReceiptFileName(string receiptId, string contentType) => "pleo-" + receiptId + contentType switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        _ when contentType.StartsWith("image/", StringComparison.Ordinal) => "." + contentType["image/".Length..],
        _ => ".bin",
    };

    // ── Classification ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies the Pleo mirror against Holded. A spend entry is in Holded when a purchase is numbered
    /// <c>Pleo-{Id}</c> (booked when approved and fully paid, unfinished otherwise) or a 57200003 line names its id. A
    /// 57200003 line naming no id is never taken as booking anything: it is a top-up (a debit whose entry has a 57200001
    /// leg, paired with a Pleo load of the same amount within <see cref="MatchDays"/> days) or unattributed.
    /// </summary>
    internal static PleoLedgerView Classify(PleoSnapshot s, DateTimeZone zone)
    {
        var open = s.Chart.Where(a => !a.Archived).Select(a => a.Number).ToHashSet();
        var names = s.Chart.ToDictionary(a => a.Number, a => a.Name);

        var docGroups = s.Purchases
            .Where(d => d.DocNumber.StartsWith(NumberPrefix, StringComparison.Ordinal))
            .GroupBy(d => d.DocNumber[NumberPrefix.Length..], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var purchases = docGroups.ToDictionary(g => g.Key, g =>
        {
            var d = g.Value.OrderByDescending(x => x.IsApproved).First();
            return new PleoPurchaseRef(d.Id, d.DocNumber, d.IsApproved, d.Total, d.PaymentsPending, d.HoldedUrl);
        }, StringComparer.Ordinal);
        var duplicates = docGroups.Where(g => g.Value.Count > 1)
            .Select(g => new PleoDuplicate(g.Key, g.Value.Select(d => d.HoldedUrl).ToList()))
            .ToList();

        var namedOnPleo = new HashSet<string>(StringComparer.Ordinal);
        var topUpLines = new List<HoldedLedgerLineInfo>();
        var unattributed = new List<HoldedLedgerLineInfo>();
        foreach (var l in s.PleoLines)
        {
            if (PleoIdOf(l.Description) is { } id) namedOnPleo.Add(id);
            else if (l.Debit > 0m && s.SabadellEntries.Contains(l.EntryNumber)) topUpLines.Add(l);
            else unattributed.Add(l);
        }

        var topUps = s.Entries.Where(IsTopUp)
            .OrderBy(e => e.SettledAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e =>
            {
                var date = OrgDate(e.SettledAt, zone);
                var amount = e.AmountEur!.Value;
                var hit = topUpLines
                    .Where(l => l.Debit == amount && Math.Abs(Period.Between(date, OrgDate(l.Date, zone), PeriodUnits.Days).Days) <= MatchDays)
                    .OrderBy(l => Math.Abs(Period.Between(date, OrgDate(l.Date, zone), PeriodUnits.Days).Days))
                    .ThenBy(l => l.EntryNumber)
                    .FirstOrDefault();
                if (hit is not null) topUpLines.Remove(hit);
                return new PleoTopUpRow(e.Id, date, amount, hit?.EntryNumber);
            })
            .ToList();
        // A Sabadell debit no Pleo load claimed is not a top-up Humans knows of.
        unattributed.AddRange(topUpLines);

        var spendEntries = s.Entries.Where(IsSpend)
            .OrderBy(e => e.SettledAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var spend = spendEntries.Select(e =>
            {
                var date = OrgDate(e.SettledAt, zone);
                var account = AccountOf(e, open);
                if (purchases.TryGetValue(e.Id, out var p))
                    return new PleoSpendRow(e, date, p.Complete ? PleoSpendState.Booked : PleoSpendState.Unfinished, account, p);
                if (namedOnPleo.Contains(e.Id)) return new PleoSpendRow(e, date, PleoSpendState.Booked, account, null);
                if (s.Claims.Contains(e.Id)) return new PleoSpendRow(e, date, PleoSpendState.CheckInHolded, account, null);
                return new PleoSpendRow(e, date, Readiness(e, open), account, null);
            })
            .ToList();

        var uploaded = s.Uploads.GroupBy(u => u.PleoEntryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(u => u.ReceiptId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var waiting = spend.Where(r => r.Purchase is not null)
            .Select(r => new PleoReceiptsWaiting(r, (r.Entry.ReceiptIds ?? [])
                .Where(id => !uploaded.TryGetValue(r.Entry.Id, out var done) || !done.Contains(id)).ToList()))
            .Where(w => w.ReceiptIds.Count > 0)
            .ToList();

        return new PleoLedgerView(
            topUps, spend, duplicates,
            unattributed,
            RefundOrigins(spendEntries),
            waiting,
            Changes(s, spend, purchases, open, names),
            s.Uploads.Where(u => !u.Confirmed).Select(u => u.PleoEntryId).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Same merchant, a charge at least as large, settled within <see cref="RefundLookbackDays"/> days before;
    /// the latest such charge.</summary>
    private static List<PleoRefundOrigin> RefundOrigins(IReadOnlyList<PleoEntryInfo> spend)
    {
        var charges = spend.Where(e => e.AmountEur < 0m && !string.IsNullOrWhiteSpace(e.MerchantName)).ToList();
        var origins = new List<PleoRefundOrigin>();
        foreach (var r in spend.Where(e => e.AmountEur > 0m && !string.IsNullOrWhiteSpace(e.MerchantName)))
        {
            var from = r.SettledAt - Duration.FromDays(RefundLookbackDays);
            var charge = charges
                .Where(c => string.Equals(c.MerchantName!.Trim(), r.MerchantName!.Trim(), StringComparison.OrdinalIgnoreCase)
                            && -c.AmountEur!.Value >= r.AmountEur!.Value
                            && c.SettledAt <= r.SettledAt && c.SettledAt >= from)
                .OrderByDescending(c => c.SettledAt)
                .FirstOrDefault();
            if (charge is not null)
                origins.Add(new PleoRefundOrigin(r.Id, charge,
                    !string.Equals(charge.CategoryId, r.CategoryId, StringComparison.Ordinal)));
        }
        return origins;
    }

    /// <summary>
    /// Per booked Pleo id, the net on each 629 account over every ledger line naming the id (the purchase's own lines
    /// and any correction), against what Pleo says now. An id with no such line yet (a purchase the ledger mirror has
    /// not read) is left alone: posting against it would book it twice.
    /// </summary>
    private static List<PleoChangeRow> Changes(
        PleoSnapshot s, IReadOnlyList<PleoSpendRow> spend, IReadOnlyDictionary<string, PleoPurchaseRef> purchases,
        IReadOnlySet<int> open, IReadOnlyDictionary<int, string> names)
    {
        var holdedById = s.ExpenseLines
            .Select(l => (Id: PleoIdOf(l.Description), Line: l))
            .Where(x => x.Id is not null)
            .GroupBy(x => x.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, decimal>)g
                .GroupBy(x => x.Line.AccountNum)
                .ToDictionary(a => a.Key, a => a.Sum(x => x.Line.Debit - x.Line.Credit))
                .Where(kv => kv.Value != 0m)
                .ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        // An id whose lines net to zero (fully corrected away) is still read; only an id with no line at all is not.
        var rows = spend.ToDictionary(r => r.Entry.Id, StringComparer.Ordinal);
        var changes = new List<PleoChangeRow>();

        foreach (var id in purchases.Keys.Union(holdedById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var read = holdedById.TryGetValue(id, out var lines);
            var held = lines ?? new Dictionary<int, decimal>();
            if (!rows.TryGetValue(id, out var row))
            {
                changes.Add(new PleoChangeRow(id, null, held, "no longer settled in Pleo; check it by hand", []));
                continue;
            }
            if (!read) continue;
            if (row.Account is not { } current || row.Entry.AmountEur is not { } amount)
            {
                changes.Add(new PleoChangeRow(id, row.Entry, held,
                    "no longer on a mapped Holded account in Pleo; check it by hand", []));
                continue;
            }
            var corrections = Corrections(id, current, -amount, held, names);
            if (corrections.Count > 0)
                changes.Add(new PleoChangeRow(id, row.Entry, held,
                    string.Join("; ", corrections.Select(c => c.Description[(c.Description.IndexOf(':') + 2)..])),
                    corrections));
        }
        return changes;
    }

    /// <summary>
    /// The journal entries that bring the ledger's net per 629 account (<paramref name="held"/>) to
    /// <paramref name="expected"/> on <paramref name="current"/> alone: an amount change first, Dr/Cr between the
    /// current account and 57200003 for the difference; then a move per old account, Dr/Cr between it and the
    /// current account. Empty when they already agree.
    /// </summary>
    internal static IReadOnlyList<PleoCorrection> Corrections(
        string pleoId, int current, decimal expected, IReadOnlyDictionary<int, decimal> held,
        IReadOnlyDictionary<int, string> names)
    {
        string Name(int n) => names.TryGetValue(n, out var name) ? $"{n} {name}" : n.ToString(CultureInfo.InvariantCulture);

        var diff = held.ToDictionary(kv => kv.Key, kv => -kv.Value);
        diff[current] = diff.GetValueOrDefault(current) + expected;
        var list = new List<PleoCorrection>();

        var total = diff.Values.Sum();
        if (total != 0m)
        {
            var was = held.Values.Sum();
            var text = $"Pleo {pleoId}: importe cambiado / amount changed {Money(was)} → {Money(expected)}";
            list.Add(total > 0m
                ? new PleoCorrection(current, PleoTreasury, total, text)
                : new PleoCorrection(PleoTreasury, current, -total, text));
            diff[current] -= total;
        }

        var debits = diff.Where(kv => kv.Value > 0m).OrderBy(kv => kv.Key).Select(kv => (Account: kv.Key, Left: kv.Value)).ToList();
        var credits = diff.Where(kv => kv.Value < 0m).OrderBy(kv => kv.Key).Select(kv => (Account: kv.Key, Left: -kv.Value)).ToList();
        int i = 0, j = 0;
        while (i < debits.Count && j < credits.Count)
        {
            var amount = Math.Min(debits[i].Left, credits[j].Left);
            var (dr, cr) = (debits[i].Account, credits[j].Account);
            var old = dr == current ? cr : dr;
            list.Add(new PleoCorrection(dr, cr, amount,
                $"Pleo {pleoId}: recategorizado / recategorised {Name(old)} → {Name(current)}"));
            debits[i] = (dr, debits[i].Left - amount);
            credits[j] = (cr, credits[j].Left - amount);
            if (debits[i].Left == 0m) i++;
            if (credits[j].Left == 0m) j++;
        }
        return list;
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PleoReconcileVm> GetReconcileAsync(CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        var view = Classify(snapshot, OrgZone);
        var names = snapshot.Chart.ToDictionary(a => a.Number, a => a.EnglishName ?? a.Name);
        var readyByAccount = view.Spend.Where(r => r.State == PleoSpendState.Ready)
            .GroupBy(r => r.Account!.Value)
            .Select(g => new PleoAccountTotalVm(g.Key, names.GetValueOrDefault(g.Key, ""), g.Count(), g.Sum(r => r.Amount)))
            .ToList();

        return new PleoReconcileVm(
            await pleo.GetWalletBalanceAsync(ct),
            (await holded.GetAccountBalancesAsync(ct)).GetValueOrDefault(PleoTreasury),
            await holded.GetLedgerSyncedAtAsync(ct),
            NotConfigured(await repo.GetSettingsAsync(ct)),
            view,
            readyByAccount,
            names,
            state.Run);
    }

    /// <summary>The Holded contact and treasury Pleo purchases use, as stored (null when unset).</summary>
    public async Task<(string? ContactId, string? TreasuryId)> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetSettingsAsync(ct);
        return (settings.PleoHoldedContactId, settings.PleoHoldedTreasuryId);
    }

    /// <summary>Blank clears a value, which switches Pleo booking off. Audited.</summary>
    public async Task SaveSettingsAsync(string? contactId, string? treasuryId, Guid actorUserId, CancellationToken ct = default)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var settings = await repo.GetSettingsAsync(ct);
        settings.PleoHoldedContactId = Clean(contactId);
        settings.PleoHoldedTreasuryId = Clean(treasuryId);
        await repo.SaveSettingsAsync(settings, ct);
        await audit.LogAsync(FinanceAuditActions.FinancePleoSettingsUpdated, "FinanceSettings", Guid.Empty,
            $"Pleo booking: Holded contact {settings.PleoHoldedContactId ?? "unset"}, treasury {settings.PleoHoldedTreasuryId ?? "unset"}",
            actorUserId);
    }

    private string? NotConfigured(FinanceSettings settings)
    {
        if (!holded.IsConfigured) return "Holded is not configured here.";
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.PleoHoldedContactId)) missing.Add("the Holded contact");
        if (string.IsNullOrWhiteSpace(settings.PleoHoldedTreasuryId)) missing.Add("the Holded treasury");
        return missing.Count == 0 ? null : $"Set {string.Join(" and ", missing)} for Pleo on /Settings (Finance tab).";
    }

    private async Task<PleoSnapshot> LoadAsync(CancellationToken ct)
    {
        var today = OrgDate(clock.GetCurrentInstant(), OrgZone);
        var entries = await pleo.GetSettledEntriesAsync(Inception, today, ct);
        var pleoLines = await holded.GetLedgerLinesAsync(PleoTreasury, ct);
        var sabadell = (await holded.GetLedgerLinesAsync(SabadellTreasury, ct)).Select(l => l.EntryNumber).ToHashSet();
        var chart = await holded.GetChartAsync(ct);
        var purchases = (await holded.GetPurchaseDocumentsAsync(ct)).Docs
            .Where(d => d.DocNumber.StartsWith(NumberPrefix, StringComparison.Ordinal))
            .ToList();
        var expenseLines = new List<HoldedLedgerLineInfo>();
        foreach (var account in chart.Where(a => IsExpenseAccount(a.Number)).Select(a => a.Number))
            expenseLines.AddRange((await holded.GetLedgerLinesAsync(account, ct)).Where(l => PleoIdOf(l.Description) is not null));
        return new PleoSnapshot(entries, pleoLines, sabadell, chart, purchases, expenseLines,
            await repo.GetPleoBookingClaimsAsync(ct), await repo.GetPleoReceiptUploadsAsync(ct));
    }

    // ── Runs ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Books (or finishes) the oldest <paramref name="limit"/> entries (all when null). A job may call this
    /// as the button does.</summary>
    public Task<PleoBookingRunVm> BookAsync(int? limit, Guid? actorUserId, CancellationToken ct = default) =>
        RunAsync(PleoRunKind.Book, limit, actorUserId, ct);

    /// <summary>Attaches every receipt Pleo has for a booked entry that Holded has not been sent.</summary>
    public Task<PleoBookingRunVm> AttachReceiptsAsync(Guid? actorUserId, CancellationToken ct = default) =>
        RunAsync(PleoRunKind.Receipts, null, actorUserId, ct);

    /// <summary>Posts the corrections of the oldest <paramref name="limit"/> changed entries (all when null).</summary>
    public Task<PleoBookingRunVm> PostCorrectionsAsync(int? limit, Guid? actorUserId, CancellationToken ct = default) =>
        RunAsync(PleoRunKind.Corrections, limit, actorUserId, ct);

    /// <summary>Refused, nothing posted, while another run of any kind holds the gate, the settings are missing, or
    /// a Holded sync is running.</summary>
    internal async Task<PleoBookingRunVm> RunAsync(PleoRunKind kind, int? limit, Guid? actorUserId, CancellationToken ct)
    {
        var started = clock.GetCurrentInstant();
        if (!await Gate.WaitAsync(0, ct))
            return new PleoBookingRunVm(kind, started, started, limit, 0, [],
                "Another Pleo run is going; nothing was done.");
        var run = new PleoBookingRunVm(kind, started, null, limit, 0, [], null);
        var lines = new List<PleoBookingLine>();
        try
        {
            state.Publish(run);

            void Progress(int? total = null)
            {
                run = run with { Total = total ?? run.Total, Lines = lines.ToList() };
                state.Publish(run);
            }

            var refused = await RunUnderGateAsync(kind, limit, actorUserId, lines, Progress, ct);
            run = run with { FinishedAt = clock.GetCurrentInstant(), Lines = lines.ToList(), Refused = refused };
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Pleo {Kind} run could not read Holded", kind);
            run = run with { FinishedAt = clock.GetCurrentInstant(), Lines = lines.ToList(), Refused = "Holded could not be read: " + ex.Message };
        }
        finally
        {
            if (run.IsRunning)
                run = run with { FinishedAt = clock.GetCurrentInstant(), Lines = lines.ToList(), Refused = "Stopped unexpectedly; see the log." };
            state.Publish(run);
            Gate.Release();
        }
        return run;
    }

    private async Task<string?> RunUnderGateAsync(
        PleoRunKind kind, int? limit, Guid? actor, List<PleoBookingLine> lines, Action<int?> progress, CancellationToken ct)
    {
        var settings = await repo.GetSettingsAsync(ct);
        if (NotConfigured(settings) is { } missing) return missing + " Nothing was done.";

        if (kind == PleoRunKind.Corrections)
        {
            if (!await holded.SyncLedgerAsync(full: false, ct))
                return "A Holded ledger sync is running; nothing was done. Try again in a minute.";
        }
        else if (!await holded.SyncPurchaseDocumentsAsync(ct))
        {
            return "A Holded purchase sync is running; nothing was done. Try again in a minute.";
        }
        if (!await holded.SyncAccountLedgerAsync(PleoTreasury, ct))
            return "A Holded ledger sync is running; nothing was done. Try again in a minute.";

        var view = Classify(await LoadAsync(ct), OrgZone);
        var refused = kind switch
        {
            PleoRunKind.Book => await BookRowsAsync(view, settings, limit, actor, lines, progress),
            PleoRunKind.Receipts => await AttachWaitingAsync(view, actor, lines, progress),
            _ => await CorrectAsync(view, limit, actor, lines, progress),
        };

        // Re-read so the page shows what landed; a failure here changes nothing that was posted.
        try
        {
            if (kind == PleoRunKind.Corrections) await holded.SyncLedgerAsync(full: false, CancellationToken.None);
            else await holded.SyncPurchaseDocumentsAsync(CancellationToken.None);
            await holded.SyncAccountLedgerAsync(PleoTreasury, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Could not re-read Holded after a Pleo {Kind} run", kind);
        }
        return refused;
    }

    private async Task<string?> BookRowsAsync(
        PleoLedgerView view, FinanceSettings settings, int? limit, Guid? actor, List<PleoBookingLine> lines,
        Action<int?> progress)
    {
        var batch = view.Spend.Where(r => r.State is PleoSpendState.Ready or PleoSpendState.Unfinished)
            .Take(limit ?? int.MaxValue).ToList();
        progress(batch.Count);
        if (batch.Count == 0) return null;

        string? ContactName(HoldedContactMirror m) =>
            m.Contacts.FirstOrDefault(c => string.Equals(c.Id, settings.PleoHoldedContactId, StringComparison.Ordinal))?.Name;
        var contactName = ContactName(await holded.GetContactsAsync(CancellationToken.None));
        if (contactName is null && await holded.SyncContactsAsync(CancellationToken.None))
            contactName = ContactName(await holded.GetContactsAsync(CancellationToken.None));
        if (contactName is null)
            return $"The Pleo contact {settings.PleoHoldedContactId} is not in Holded's contact list; nothing was booked.";
        var uploads = (await repo.GetPleoReceiptUploadsAsync(CancellationToken.None))
            .Select(u => (u.PleoEntryId, u.ReceiptId)).ToHashSet();
        var accountNames = (await holded.GetChartAsync(CancellationToken.None)).ToDictionary(a => a.Number, a => a.Name);

        foreach (var row in batch)
        {
            // Never the request's token: once Holded has a write, abandoning the call only hides it.
            var (line, stop) = await BookOneAsync(row, settings, contactName, accountNames, uploads);
            lines.Add(line);
            await AuditAsync(FinanceAuditActions.PleoEntryBooked,
                $"Pleo {row.Entry.Id} ({row.Entry.MerchantName}, {row.Date.ToInvariantDate()}): purchase {Number(row.Entry.Id)} "
                + $"on {row.Account} for {row.Amount.ToEuro()}, paid from {PleoTreasury}, {line.Receipts} receipt(s) — "
                + $"{line.Outcome}{(line.Error is null ? "" : ": " + line.Error)}", actor);
            progress(null);
            if (stop) return "Stopped: Holded did not answer.";
        }
        return null;
    }

    /// <summary>Creates (or finishes) one entry's purchase: create, approve, pay in full from 57200003, attach its
    /// receipts. <c>Stop</c> is true when Holded did not answer a write that may have landed.</summary>
    private async Task<(PleoBookingLine Line, bool Stop)> BookOneAsync(
        PleoSpendRow row, FinanceSettings settings, string contactName, IReadOnlyDictionary<int, string> accountNames,
        HashSet<(string, string)> uploads)
    {
        var e = row.Entry;
        var amount = e.AmountEur!.Value;
        // A charge is a purchase of |amount|; a refund the same purchase with a negative price, paid back into Pleo.
        var total = -amount;
        var docId = row.Purchase?.DocumentId;
        var approved = row.Purchase?.Approved ?? false;
        // A draft carries no payment and Holded omits its balance, so an unapproved purchase still owes all of it.
        var pending = approved ? row.Purchase!.PaymentsPending : total;
        var outcome = PleoBookingOutcome.Posted;
        var notes = new List<string>();
        var account = row.Account ?? 0;

        PleoBookingLine Line(PleoBookingOutcome o, int receipts, string? error) =>
            new(e.Id, row.Date, e.MerchantName, account, amount, o, row.Purchase?.HoldedUrl, receipts, error);

        try
        {
            if (docId is null)
            {
                // The account is open in the chart mirror, so this only reads its Holded id from there.
                var accountId = (await write.EnsureExpenseAccountAsync(
                    account, accountNames.GetValueOrDefault(account, ""), CancellationToken.None)).HoldedId;
                if (!await repo.TryClaimPleoBookingAsync(e.Id, clock.GetCurrentInstant(), CancellationToken.None))
                    return (Line(PleoBookingOutcome.Skipped, 0, "already claimed by an earlier run: check in Holded"), false);
                try
                {
                    docId = await write.CreatePurchaseDocumentAsync(new HoldedPurchaseDocumentInput
                    {
                        ContactId = settings.PleoHoldedContactId!,
                        ContactName = contactName,
                        Number = Number(e.Id),
                        Date = row.Date.AtStartOfDayInZone(OrgZone).ToInstant(),
                        Description = DocDescription(e),
                        Notes = DocNotes(e),
                        Lines = [new HoldedPurchaseDocumentLineInput
                        {
                            Description = LineName(e), Amount = total, AccountId = accountId, Taxes = [],
                        }],
                    }, CancellationToken.None);
                }
                catch (HoldedPermanentException ex) when (ex.StatusCode is >= 400 and < 500)
                {
                    // Refused outright: nothing was created, so the entry may be booked again. A create Holded accepted
                    // but answered unreadably (no status code) keeps its claim: the purchase may exist.
                    await repo.ReleasePleoBookingClaimAsync(e.Id, CancellationToken.None);
                    throw;
                }
            }

            if (!approved) await write.EnsurePurchaseDocumentApprovedAsync(docId, CancellationToken.None);

            if (pending is null)
            {
                notes.Add("Holded's list does not say what is left to pay: check the payment in Holded");
                outcome = PleoBookingOutcome.Unconfirmed;
            }
            else if (pending != 0m)
            {
                var paymentRef = await write.PayPurchaseDocumentAsync(docId, pending.Value, settings.PleoHoldedTreasuryId,
                    row.Date, "Pago con tarjeta Pleo / Pleo card payment", CancellationToken.None);
                if (paymentRef.StartsWith(UnconfirmedRef, StringComparison.Ordinal))
                {
                    notes.Add("Holded took the payment without an id: check it in Holded");
                    outcome = PleoBookingOutcome.Unconfirmed;
                }
            }

            var (attached, receiptNotes, receiptStop) = await AttachAsync(e, docId, uploads);
            notes.AddRange(receiptNotes);
            if (receiptStop) outcome = PleoBookingOutcome.Unconfirmed;
            return (Line(outcome, attached, notes.Count == 0 ? null : string.Join("; ", notes)), receiptStop);
        }
        catch (HoldedPermanentException ex)
        {
            logger.LogWarning("Holded refused Pleo entry {PleoId} ({Amount} on {Account}): {Reason}", e.Id, amount, account, ex.Message);
            return (Line(PleoBookingOutcome.Failed, 0,
                (docId is null ? "Holded refused it: " : "Holded refused a step; the next run finishes it: ") + ex.Message), false);
        }
        catch (HoldedTransientException ex)
        {
            logger.LogWarning("Holded did not answer for Pleo entry {PleoId}; it may have landed: {Reason}", e.Id, ex.Message);
            return (Line(PleoBookingOutcome.Failed, 0,
                $"Holded did not answer ({ex.Message}): it may have landed, check in Holded"), true);
        }
    }

    /// <summary>Sends each receipt of <paramref name="e"/> not yet sent, named <c>pleo-{receiptId}</c>. A receipt Pleo
    /// can't serve, or one over 10 MB, is reported and left for a later run; one Holded did not answer is recorded
    /// unconfirmed and never sent again.</summary>
    private async Task<(int Attached, List<string> Notes, bool Stop)> AttachAsync(
        PleoEntryInfo e, string docId, HashSet<(string, string)> uploads)
    {
        var attached = 0;
        var notes = new List<string>();
        foreach (var receiptId in (e.ReceiptIds ?? []).Where(r => !uploads.Contains((e.Id, r))))
        {
            var file = await pleo.GetReceiptFileAsync(e.Id, receiptId, CancellationToken.None);
            if (file is null)
            {
                notes.Add($"receipt {receiptId} could not be fetched from Pleo; attach waiting receipts later");
                continue;
            }
            if (file.Content.Length > MaxAttachmentBytes)
            {
                notes.Add($"receipt {receiptId} is over 10 MB; attach it by hand");
                continue;
            }
            try
            {
                using var content = new MemoryStream(file.Content, writable: false);
                await write.UploadPurchaseAttachmentAsync(docId, new HoldedAttachmentInput
                {
                    FileName = ReceiptFileName(receiptId, file.ContentType),
                    ContentType = file.ContentType,
                    Content = content,
                }, CancellationToken.None);
            }
            catch (HoldedTransientException ex)
            {
                await RecordUploadAsync(e.Id, receiptId, confirmed: false);
                uploads.Add((e.Id, receiptId));
                notes.Add($"Holded did not answer the upload of receipt {receiptId} ({ex.Message}): check the purchase in Holded");
                return (attached, notes, true);
            }
            await RecordUploadAsync(e.Id, receiptId, confirmed: true);
            uploads.Add((e.Id, receiptId));
            attached++;
        }
        return (attached, notes, false);
    }

    private Task RecordUploadAsync(string pleoId, string receiptId, bool confirmed) =>
        repo.AddPleoReceiptUploadAsync(new PleoReceiptUpload
        {
            PleoEntryId = pleoId,
            ReceiptId = receiptId,
            UploadedAt = clock.GetCurrentInstant(),
            Confirmed = confirmed,
        }, CancellationToken.None);

    private async Task<string?> AttachWaitingAsync(
        PleoLedgerView view, Guid? actor, List<PleoBookingLine> lines, Action<int?> progress)
    {
        progress(view.ReceiptsWaiting.Count);
        var uploads = (await repo.GetPleoReceiptUploadsAsync(CancellationToken.None))
            .Select(u => (u.PleoEntryId, u.ReceiptId)).ToHashSet();
        foreach (var w in view.ReceiptsWaiting)
        {
            var row = w.Row;
            PleoBookingLine line;
            var stop = false;
            try
            {
                var (attached, notes, receiptStop) = await AttachAsync(row.Entry, row.Purchase!.DocumentId, uploads);
                stop = receiptStop;
                line = new PleoBookingLine(row.Entry.Id, row.Date, row.Entry.MerchantName, row.Account ?? 0, row.Amount,
                    stop ? PleoBookingOutcome.Unconfirmed : attached > 0 ? PleoBookingOutcome.Posted : PleoBookingOutcome.Failed,
                    row.Purchase.HoldedUrl, attached, notes.Count == 0 ? null : string.Join("; ", notes));
            }
            catch (HoldedPermanentException ex)
            {
                line = new PleoBookingLine(row.Entry.Id, row.Date, row.Entry.MerchantName, row.Account ?? 0, row.Amount,
                    PleoBookingOutcome.Failed, row.Purchase!.HoldedUrl, 0, "Holded refused the upload: " + ex.Message);
            }
            lines.Add(line);
            if (line.Receipts > 0 || line.Outcome == PleoBookingOutcome.Unconfirmed)
                await AuditAsync(FinanceAuditActions.PleoReceiptAttached,
                $"Pleo {row.Entry.Id} ({row.Entry.MerchantName}): {line.Receipts} receipt(s) attached to purchase "
                    + $"{row.Purchase!.Number}{(line.Error is null ? "" : " — " + line.Error)}", actor);
            progress(null);
            if (stop) return "Stopped: Holded did not answer.";
        }
        return null;
    }

    private async Task<string?> CorrectAsync(
        PleoLedgerView view, int? limit, Guid? actor, List<PleoBookingLine> lines, Action<int?> progress)
    {
        var batch = view.Changes.Where(c => c.Corrections.Count > 0).Take(limit ?? int.MaxValue).ToList();
        progress(batch.Count);
        var today = OrgDate(clock.GetCurrentInstant(), OrgZone);
        foreach (var change in batch)
        {
            var e = change.Entry!;
            foreach (var c in change.Corrections)
            {
                PleoBookingLine line;
                var stop = false;
                try
                {
                    var posted = await write.PostJournalEntryAsync(today, c.Debit, c.Credit, c.Amount, c.Description,
                        CancellationToken.None);
                    line = Corrected(posted.Confirmed ? PleoBookingOutcome.Posted : PleoBookingOutcome.Unconfirmed,
                        posted.Confirmed ? null : "Holded took it without an id: check in Holded");
                }
                catch (HoldedPermanentException ex)
                {
                    line = Corrected(PleoBookingOutcome.Failed, "Holded refused it: " + ex.Message);
                }
                catch (HoldedTransientException ex)
                {
                    line = Corrected(PleoBookingOutcome.Failed, $"Holded did not answer ({ex.Message}): it may have posted, check in Holded");
                    stop = true;
                }
                lines.Add(line);
                await AuditAsync(FinanceAuditActions.PleoCorrectionPosted,
                $"{c.Description}: Dr {c.Debit} / Cr {c.Credit} {c.Amount.ToEuro()} — {line.Outcome}"
                    + (line.Error is null ? "" : ": " + line.Error), actor);
                progress(null);
                if (stop) return "Stopped: Holded did not answer.";

                PleoBookingLine Corrected(PleoBookingOutcome o, string? error) =>
                    new(e.Id, today, e.MerchantName, c.Debit == PleoTreasury ? c.Credit : c.Debit, e.AmountEur ?? 0m, o, null, 0,
                        error is null ? c.Description : c.Description + " — " + error);
            }
        }
        return null;
    }

    private Task AuditAsync(AuditActionDefinition action, string text, Guid? actor) =>
        actor is { } a
            ? audit.LogAsync(action, AuditEntityType, Guid.Empty, text, a)
            : audit.LogAsync(action, AuditEntityType, Guid.Empty, text, JobName);
}
