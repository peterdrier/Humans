using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Domain;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Pure over local snapshots, so a test seeds it directly. Matching is <see cref="HoldedDuplicateMatcher"/>'s, with the
/// mirrored notes as the tiebreak the live duplicate screen gets from a GET, over the same member contact set.
/// </summary>
internal static class ExpenseRecon
{
    // The push's markers (Expenses' ExpenseHoldedPublisher.DocNotes/DocDescription, internal to that section).
    private const string NotesMarker = "Expense report ";
    internal const string DescriptionMarker = "ER: ";

    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    internal static ExpenseReconReport Unavailable(int? year, string reason) =>
        new(year, null, reason, [], [], [], [], new ExpenseReconTotals(0, 0, 0, 0, 0, 0m, 0, 0));

    internal static ExpenseReconReport Build(
        int? year,
        Instant mirrorSyncedAt,
        IReadOnlyList<ExpenseHoldedReconLine> expected,
        IReadOnlyList<HoldedExpenseDoc> docs,
        IReadOnlyList<HoldedCreditorContact> bindings,
        IReadOnlyDictionary<int, decimal> balances,
        IReadOnlyList<SepaPayoutTransferRow> transfers,
        Func<Guid, string> memberName)
    {
        var docById = docs.GroupBy(d => d.HoldedDocId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bindingByUser = bindings.GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.First());
        var boundContacts = bindings.Select(b => b.HoldedContactId)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToHashSet(StringComparer.Ordinal);

        var duplicatesByLine = FindDuplicates(expected, docs, docById, bindings);
        var claimed = expected.Where(l => l.HoldedDocId is not null).Select(l => l.HoldedDocId!)
            .Concat(duplicatesByLine.Values.SelectMany(d => d.Select(x => x.Id)))
            .ToHashSet(StringComparer.Ordinal);

        var allLines = expected.Select(l =>
        {
            var doc = l.HoldedDocId is { } id ? docById.GetValueOrDefault(id) : null;
            var duplicates = l.LineId is { } lineId && duplicatesByLine.TryGetValue(lineId, out var d) ? d : [];
            var status = doc is null
                ? IsPending(l, mirrorSyncedAt) ? ExpenseReconLineStatus.Pending : ExpenseReconLineStatus.Missing
                : duplicates.Count > 0 ? ExpenseReconLineStatus.Duplicated
                : doc.Total != l.BookedAmount ? ExpenseReconLineStatus.AmountMismatch
                // After a sync ContactId comes from the list response, so null is a genuinely contactless document.
                : doc.ContactId is not { } contact
                  || !string.Equals(
                      bindingByUser.GetValueOrDefault(l.CreditorUserId)?.HoldedContactId, contact,
                      StringComparison.Ordinal)
                    ? ExpenseReconLineStatus.WrongContact
                    : ExpenseReconLineStatus.Booked;
            return new ExpenseReconLine(
                l.ExpenseReportId, l.LineId, l.CreditorUserId, memberName(l.CreditorUserId), l.Description,
                l.BookedAmount, l.DocumentDate.InZone(MadridZone).Date, l.SyncState.ToString(), status,
                l.HoldedDocId, doc?.DocNumber, doc?.Total, doc?.ContactName,
                duplicates.Select(x => x.Id).ToList(), duplicates.Sum(x => x.Total));
        }).ToList();

        var allOrphans = docs
            .Where(d => !claimed.Contains(d.HoldedDocId) && CarriesMarker(d))
            .Select(d => new ExpenseReconOrphan(
                d.HoldedDocId, d.DocNumber, d.Date, d.ContactName, d.Description, d.Notes, d.Total,
                d.ContactId is { } c && boundContacts.Contains(c)
                    ? ExpenseReconOrphanKind.NoMatchingLine
                    : ExpenseReconOrphanKind.UnboundContact))
            .ToList();

        var members = Members(allLines, bindingByUser, balances, transfers, memberName);

        bool InYear(LocalDate date) => year is null || date.Year == year;
        var lines = allLines.Where(l => InYear(l.DocumentDate)).ToList();
        var orphans = allOrphans.Where(o => InYear(o.Date)).ToList();

        var totals = new ExpenseReconTotals(
            Reports: lines.Select(l => l.ExpenseReportId).Distinct().Count(),
            Lines: lines.Count,
            HoldedDocs: lines.Count(l => l.HoldedTotal is not null) + lines.Sum(l => l.DuplicateDocIds.Count)
                        + orphans.Count,
            ProblemLines: lines.Count(l => l.IsProblem),
            DuplicateDocs: lines.Sum(l => l.DuplicateDocIds.Count),
            ExcessAmount: lines.Sum(l => l.ExcessAmount),
            Orphans: orphans.Count,
            MembersOff: members.Count(m => m.IsWarning));

        var years = allLines.Select(l => l.DocumentDate.Year).Concat(allOrphans.Select(o => o.Date.Year))
            .Distinct().OrderDescending().ToList();

        return new ExpenseReconReport(year, mirrorSyncedAt, null, years, lines, orphans, members, totals);
    }

    /// <summary>Legacy report-level documents are left out: they predate per-line descriptions and never raced.</summary>
    private static Dictionary<Guid, IReadOnlyList<HoldedPurchaseDocListItemDto>> FindDuplicates(
        IReadOnlyList<ExpenseHoldedReconLine> expected,
        IReadOnlyList<HoldedExpenseDoc> docs,
        IReadOnlyDictionary<string, HoldedExpenseDoc> docById,
        IReadOnlyList<HoldedCreditorContact> bindings)
    {
        var bookings = expected
            .Where(l => l.LineId is not null && l.HoldedDocId is not null)
            .Select(l => new ExpenseHoldedBooking(
                l.ExpenseReportId, l.LineId!.Value, l.CreditorUserId, l.HoldedContactId, l.HoldedDocId!,
                l.Description, l.Notes, l.BookedAmount))
            .ToList();

        // The mirror stands in for the contact list: a twin is another contact carrying the same name on its documents.
        var nameById = docs.Where(d => !string.IsNullOrEmpty(d.ContactId))
            .GroupBy(d => d.ContactId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ContactName, StringComparer.Ordinal);
        var byName = nameById.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToLookup(kv => kv.Value.Trim(), kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        var contacts = HoldedDuplicateMatcher.MemberContacts(
            bookings.Select(b => (b.CreditorUserId, b.HoldedContactId)), bindings,
            id => nameById.GetValueOrDefault(id), byName);

        // Unknown notes (not fetched yet) fall back to the list-level rule.
        return HoldedDuplicateMatcher.Find(
                bookings, docs.Select(ToListItem).ToList(), contacts,
                (b, d) => HoldedDuplicateMatcher.Matches(b, d)
                          && docById[d.Id].Notes is var notes && (notes is null || string.Equals(notes, b.Notes, StringComparison.Ordinal)))
            .ToDictionary(m => m.Booking.LineId, m => m.Duplicates);
    }

    /// <summary>Nothing has failed: the push has not run yet, or ran after the mirror last synced.</summary>
    private static bool IsPending(ExpenseHoldedReconLine line, Instant mirrorSyncedAt) =>
        line.HoldedDocId is null
            ? line.SyncState is ExpenseHoldedSyncState.Queued or ExpenseHoldedSyncState.Retrying
            : line.SyncSettledAt is { } settled && settled > mirrorSyncedAt;

    private static bool CarriesMarker(HoldedExpenseDoc d) =>
        (d.Notes?.StartsWith(NotesMarker, StringComparison.Ordinal) ?? false)
        || (d.Description?.StartsWith(DescriptionMarker, StringComparison.Ordinal) ?? false);

    /// <summary>
    /// All-time, whatever the year filter: the ledger balance is. A line counts once its document is in the mirror (a
    /// pending or missing one is not in the ledger either), at the booked amount, so duplicates and mismatches show as
    /// the difference.
    /// </summary>
    private static List<ExpenseReconMember> Members(
        IReadOnlyList<ExpenseReconLine> lines,
        IReadOnlyDictionary<Guid, HoldedCreditorContact> bindingByUser,
        IReadOnlyDictionary<int, decimal> balances,
        IReadOnlyList<SepaPayoutTransferRow> transfers,
        Func<Guid, string> memberName)
    {
        var transfersByAccount = transfers.Where(t => !t.IsWithdrawn).ToLookup(t => t.SupplierAccountNum);
        return lines.GroupBy(l => l.CreditorUserId).Select(g =>
            {
                var account = bindingByUser.GetValueOrDefault(g.Key)?.SupplierAccountNum;
                var accountTransfers = account is { } a ? transfersByAccount[a].ToList() : [];
                var expectedBalance = accountTransfers.Where(t => t.IsBooked).Sum(t => t.Amount)
                                      - g.Where(l => l.HoldedTotal is not null).Sum(l => l.ExpectedAmount);
                var ledger = account is { } n ? balances.GetValueOrDefault(n) : 0m;
                var status = account is null ? ExpenseReconMemberStatus.NoAccount
                    : Math.Abs(ledger - expectedBalance) < 0.005m ? ExpenseReconMemberStatus.Balanced
                    : ExpenseReconMemberStatus.Off;
                return new ExpenseReconMember(
                    g.Key, memberName(g.Key), account, ledger, expectedBalance, status,
                    accountTransfers.Where(t => !t.IsBooked)
                        .OrderBy(t => t.GeneratedAt)
                        .Select(t => new ExpenseReconOpenTransfer(t.TransferId, t.FileName, t.GeneratedAt, t.Amount))
                        .ToList());
            })
            .ToList();
    }

    private static HoldedPurchaseDocListItemDto ToListItem(HoldedExpenseDoc d) => new()
    {
        Id = d.HoldedDocId,
        DocNumber = d.DocNumber,
        ContactId = d.ContactId,
        ContactName = d.ContactName,
        Description = d.Description,
        Date = d.Date.AtStartOfDayInZone(MadridZone).ToInstant(),
        Subtotal = d.Subtotal,
        Tax = d.Tax,
        Total = d.Total,
    };
}
