using System.Text.RegularExpressions;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Pure over local snapshots, so a test seeds it directly. A line's identity is its account, description and amount,
/// matched by count against the ledger's purchase lines: stored document ids are never read, so a cleanup that
/// deletes one of two twins leaves the line matched to the survivor.
/// </summary>
internal static partial class ExpenseRecon
{
    /// <summary>What the push writes in front of a line's description (Expenses' ExpenseHoldedPublisher.DocDescription,
    /// internal to that section).</summary>
    internal const string DescriptionMarker = "ER: ";

    // Documents of 21 Sep 2026 predate the marker: "<line description> — expense report <report id>".
    private const string OldReportMarker = " — expense report ";

    private const string PurchaseType = "purchase";

    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    internal static ExpenseReconReport Unavailable(int? year, string reason) =>
        new(year, null, reason, [], [], [], [], new ExpenseReconTotals(0, 0, 0, 0, 0, 0m, 0, 0));

    /// <summary><paramref name="accountByUser"/> is the account each bound member's contact is on;
    /// <paramref name="ledger"/> holds every line of every creditor account, bound or not.</summary>
    internal static ExpenseReconReport Build(
        int? year,
        Instant mirrorSyncedAt,
        IReadOnlyList<ExpenseHoldedReconLine> expected,
        IReadOnlyDictionary<Guid, int> accountByUser,
        IReadOnlyDictionary<int, IReadOnlyList<HoldedLedgerLineInfo>> ledger,
        IReadOnlyList<SepaPayoutTransferRow> transfers,
        Func<Guid, string> memberName)
    {
        var purchases = ledger.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(IsPurchase).Select(l => new Purchase(l, Text(l.Description))).ToList());

        // Oldest first, so when twins differ only in date the earlier line claims the earlier ledger line.
        var ordered = expected.OrderBy(l => l.DocumentDate).ThenBy(l => l.ExpenseReportId).ThenBy(l => l.LineId).ToList();
        var matches = new Dictionary<ExpenseHoldedReconLine, Purchase>(ReferenceEqualityComparer.Instance);
        foreach (var line in ordered)
        {
            if (AccountOf(line) is not { } account) continue;
            var hit = purchases.GetValueOrDefault(account)?.FirstOrDefault(p => p.ClaimedBy is null && Matches(line, p));
            if (hit is null) continue;
            hit.ClaimedBy = line;
            matches[line] = hit;
        }

        // The same count rule: a marked line left over that a matched line would have taken is that line's twin.
        var duplicates = new Dictionary<ExpenseHoldedReconLine, List<Purchase>>(ReferenceEqualityComparer.Instance);
        foreach (var p in purchases.Values.SelectMany(ps => ps).Where(p => p.ClaimedBy is null && IsMarked(p.Text)))
        {
            var owner = matches.Keys.FirstOrDefault(l => AccountOf(l) == p.Line.AccountNum && Matches(l, p));
            if (owner is null) continue;
            p.DuplicateOf = owner;
            (duplicates.TryGetValue(owner, out var list) ? list : duplicates[owner] = []).Add(p);
        }

        var allLines = expected.Select(l =>
        {
            var hit = matches.GetValueOrDefault(l);
            var extra = duplicates.GetValueOrDefault(l) ?? [];
            var status = hit is null
                ? IsPending(l, mirrorSyncedAt) ? ExpenseReconLineStatus.Pending : ExpenseReconLineStatus.Missing
                : extra.Count > 0 ? ExpenseReconLineStatus.Duplicated
                : ExpenseReconLineStatus.Booked;
            return new ExpenseReconLine(
                l.ExpenseReportId, l.LineId, l.CreditorUserId, memberName(l.CreditorUserId), l.Description,
                l.BookedAmount, l.DocumentDate.InZone(MadridZone).Date, l.SyncState.ToString(), status, l.HoldedDocId,
                hit?.Line.AccountNum, hit?.Line.EntryNumber, hit is null ? null : DateOf(hit.Line),
                extra.Select(x => x.Line.EntryNumber).ToList(), extra.Sum(x => x.Amount));
        }).ToList();

        var boundAccounts = accountByUser.Values.ToHashSet();
        var allOrphans = purchases.Values.SelectMany(ps => ps)
            .Where(p => p.ClaimedBy is null && p.DuplicateOf is null && IsMarked(p.Text))
            .Select(p => new ExpenseReconOrphan(
                p.Line.AccountNum, p.Line.EntryNumber, DateOf(p.Line), p.Line.Description ?? "", p.Amount,
                boundAccounts.Contains(p.Line.AccountNum)
                    ? ExpenseReconOrphanKind.NoMatchingLine
                    : ExpenseReconOrphanKind.UnboundContact))
            .ToList();

        var members = Members(allLines, accountByUser, ledger, purchases, transfers, memberName);

        bool InYear(LocalDate date) => year is null || date.Year == year;
        var lines = allLines.Where(l => InYear(l.DocumentDate)).ToList();
        var orphans = allOrphans.Where(o => InYear(o.Date)).ToList();

        var totals = new ExpenseReconTotals(
            Reports: lines.Select(l => l.ExpenseReportId).Distinct().Count(),
            Lines: lines.Count,
            LedgerPurchases: lines.Count(l => l.LedgerEntryNumber is not null)
                             + lines.Sum(l => l.DuplicateEntryNumbers.Count) + orphans.Count,
            ProblemLines: lines.Count(l => l.IsProblem),
            DuplicateLines: lines.Sum(l => l.DuplicateEntryNumbers.Count),
            ExcessAmount: lines.Sum(l => l.ExcessAmount),
            Orphans: orphans.Count,
            MembersOff: members.Count(m => m.IsWarning));

        var years = allLines.Select(l => l.DocumentDate.Year).Concat(allOrphans.Select(o => o.Date.Year))
            .Distinct().OrderDescending().ToList();

        return new ExpenseReconReport(year, mirrorSyncedAt, null, years, lines, orphans, members, totals);

        int? AccountOf(ExpenseHoldedReconLine l) => accountByUser.TryGetValue(l.CreditorUserId, out var a) ? a : null;
    }

    /// <summary>Amount always; then the description the push wrote, or for a legacy report-level document (which
    /// predates per-line descriptions) the report id.</summary>
    private static bool Matches(ExpenseHoldedReconLine line, Purchase p)
    {
        if (p.Amount != line.BookedAmount) return false;
        var reportId = line.ExpenseReportId.ToString();
        if (line.LineId is null)
            return p.Text.Contains(reportId, StringComparison.OrdinalIgnoreCase);

        var description = Normalize(line.Description);
        if (EndsWithField(p.Text, description)) return true;
        var bare = description.StartsWith(DescriptionMarker, StringComparison.Ordinal)
            ? description[DescriptionMarker.Length..]
            : description;
        return p.Text.EndsWith(bare + OldReportMarker + reportId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWithField(string text, string field) =>
        string.Equals(text, field, StringComparison.Ordinal)
        || text.EndsWith(", " + field, StringComparison.Ordinal);

    private static bool IsMarked(string text) =>
        text.StartsWith(DescriptionMarker, StringComparison.Ordinal)
        || text.Contains(", " + DescriptionMarker, StringComparison.Ordinal)
        || OldFormat().IsMatch(text);

    /// <summary>Holded writes a document's ledger line as <c>&lt;Purchase|Compra&gt;, &lt;contact&gt;, &lt;doc
    /// description&gt;, dd/mm/yyyy</c>, trimmed and with runs of spaces collapsed; this drops the date and does the
    /// same to both sides.</summary>
    private static string Text(string? description) => TrailingDate().Replace(Normalize(description ?? ""), "");

    private static string Normalize(string s) => Whitespace().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@", \d{2}/\d{2}/\d{4}$", RegexOptions.NonBacktracking)]
    private static partial Regex TrailingDate();

    [GeneratedRegex(@" — expense report [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex OldFormat();

    private static bool IsPurchase(HoldedLedgerLineInfo l) =>
        string.Equals(l.Type, PurchaseType, StringComparison.OrdinalIgnoreCase);

    private static LocalDate DateOf(HoldedLedgerLineInfo l) => l.Date.InZone(MadridZone).Date;

    /// <summary>Nothing has failed: the push has not run yet, or ran after the mirror last synced.</summary>
    private static bool IsPending(ExpenseHoldedReconLine line, Instant mirrorSyncedAt) =>
        line.HoldedDocId is null
            ? line.SyncState is ExpenseHoldedSyncState.Queued or ExpenseHoldedSyncState.Retrying
            : line.SyncSettledAt is { } settled && settled > mirrorSyncedAt;

    /// <summary>
    /// All-time, whatever the year filter: the ledger is. Each account's lines split into the expense purchases
    /// matched above, marked purchases nobody claims, booked SEPA payments, and the rest. A booked transfer's payments
    /// are all dated the bank line, so it claims the first day of non-purchase debits, between generation and booking,
    /// that sums to its amount.
    /// </summary>
    private static List<ExpenseReconMember> Members(
        IReadOnlyList<ExpenseReconLine> lines,
        IReadOnlyDictionary<Guid, int> accountByUser,
        IReadOnlyDictionary<int, IReadOnlyList<HoldedLedgerLineInfo>> ledger,
        IReadOnlyDictionary<int, List<Purchase>> purchases,
        IReadOnlyList<SepaPayoutTransferRow> transfers,
        Func<Guid, string> memberName)
    {
        var transfersByAccount = transfers.Where(t => !t.IsWithdrawn).ToLookup(t => t.SupplierAccountNum);
        return lines.GroupBy(l => l.CreditorUserId).Select(g =>
            {
                var missing = g.Count(l => l.Status == ExpenseReconLineStatus.Missing);
                if (!accountByUser.TryGetValue(g.Key, out var account))
                    return new ExpenseReconMember(
                        g.Key, memberName(g.Key), null, 0m, 0m, 0m, 0m, missing, 0, [], [],
                        ExpenseReconMemberStatus.NoAccount, []);

                var accountPurchases = purchases.GetValueOrDefault(account) ?? [];
                var matched = accountPurchases.Where(p => p.ClaimedBy is not null).ToList();
                var unclaimed = accountPurchases
                    .Where(p => p.ClaimedBy is null && IsMarked(p.Text)).ToList();
                var accounted = matched.Concat(unclaimed).Select(p => p.Line).ToHashSet();

                var rest = (ledger.GetValueOrDefault(account) ?? []).Where(l => !accounted.Contains(l)).ToList();
                var debitDays = rest.Where(l => !IsPurchase(l) && l.Debit > 0m)
                    .GroupBy(DateOf)
                    .OrderBy(d => d.Key)
                    .Select(d => (Date: d.Key, Lines: d.ToList(), Sum: d.Sum(l => l.Debit - l.Credit)))
                    .ToList();
                var sepaLines = new HashSet<HoldedLedgerLineInfo>();
                var unmatchedTransfers = new List<ExpenseReconOpenTransfer>();
                var sepaPaid = 0m;
                foreach (var t in transfersByAccount[account].Where(t => t.IsBooked).OrderBy(t => t.GeneratedAt))
                {
                    var from = t.GeneratedAt.InZone(MadridZone).Date.PlusDays(-1);
                    var to = t.BookedAt!.Value.InZone(MadridZone).Date.PlusDays(1);
                    var day = debitDays.FirstOrDefault(d =>
                        d.Date >= from && d.Date <= to && d.Sum == t.Amount && !d.Lines.Exists(sepaLines.Contains));
                    if (day.Lines is null)
                    {
                        unmatchedTransfers.Add(ToTransfer(t));
                        continue;
                    }
                    sepaLines.UnionWith(day.Lines);
                    sepaPaid += day.Sum;
                }

                var other = rest.Where(l => !sepaLines.Contains(l))
                    .Select(l => new ExpenseReconMovement(DateOf(l), l.EntryNumber, l.Type, l.Description, l.Debit - l.Credit))
                    .ToList();
                var status = missing == 0 && unclaimed.Count == 0 && unmatchedTransfers.Count == 0
                    ? ExpenseReconMemberStatus.Balanced
                    : ExpenseReconMemberStatus.Off;
                return new ExpenseReconMember(
                    g.Key, memberName(g.Key), account,
                    LedgerBalance: (ledger.GetValueOrDefault(account) ?? []).Sum(l => l.Debit - l.Credit),
                    ExpensePurchases: -matched.Sum(p => p.Amount),
                    SepaPaid: sepaPaid,
                    UnclaimedPurchases: -unclaimed.Sum(p => p.Amount),
                    MissingLines: missing,
                    UnclaimedPurchaseCount: unclaimed.Count,
                    UnmatchedTransfers: unmatchedTransfers,
                    OtherMovements: other,
                    Status: status,
                    OpenTransfers: transfersByAccount[account].Where(t => !t.IsBooked)
                        .OrderBy(t => t.GeneratedAt).Select(ToTransfer).ToList());
            })
            .ToList();
    }

    private static ExpenseReconOpenTransfer ToTransfer(SepaPayoutTransferRow t) =>
        new(t.TransferId, t.FileName, t.GeneratedAt, t.Amount);

    /// <summary>A ledger purchase line; <see cref="Amount"/> is credit − debit, positive owed to the account.</summary>
    private sealed class Purchase(HoldedLedgerLineInfo line, string text)
    {
        public HoldedLedgerLineInfo Line { get; } = line;
        public string Text { get; } = text;
        public decimal Amount { get; } = line.Credit - line.Debit;
        public ExpenseHoldedReconLine? ClaimedBy { get; set; }
        public ExpenseHoldedReconLine? DuplicateOf { get; set; }
    }
}
