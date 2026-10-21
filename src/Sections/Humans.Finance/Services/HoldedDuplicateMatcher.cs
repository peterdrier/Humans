using Humans.Expenses.Contracts;
using Humans.Holded.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// Finds Holded purchase documents the expense push created twice (the outbox race of
/// 2026-10-18). Line-to-document: for each expense line that links to a document, every other
/// document on the member's contacts that carries the same description and the line's booked total
/// is a candidate duplicate of it. Notes are not part of this rule: the v2 purchase list never
/// returns them (verified live 2026-10-21), so the page and the apply confirm each candidate's
/// notes with a single GET before it can be deleted. Two lines with identical values legitimately
/// produce two documents, each the kept document of its own line, so a document some line links to
/// is never anyone's duplicate, and an extra document matching several identical lines is the
/// duplicate of the first of them only, so it is counted and deleted once. Pure, so the nightly
/// check, the payout gate, the page and the apply all run the same rule.
/// </summary>
internal static class HoldedDuplicateMatcher
{
    /// <summary>One linked line with the documents that duplicate it.</summary>
    internal sealed record Match(
        ExpenseHoldedBooking Booking,
        HoldedPurchaseDocListItemDto Kept,
        IReadOnlyList<HoldedPurchaseDocListItemDto> Duplicates);

    /// <summary>Whether <paramref name="doc"/> carries what the list shows of the push's write for
    /// <paramref name="booking"/>: description and total. A false positive here only refuses a
    /// payout; a delete needs the notes confirmed by the single GET first.</summary>
    internal static bool Matches(ExpenseHoldedBooking booking, HoldedPurchaseDocListItemDto doc) =>
        string.Equals(doc.Description, booking.Description, StringComparison.Ordinal)
        && doc.Total == booking.BookedAmount;

    /// <summary>Every line with at least one duplicate. <paramref name="contactsByUser"/> widens
    /// the search from the line's own contact to every contact the member holds (the race also
    /// created a second contact for three members); a member with no entry is searched on the
    /// contacts their bookings name. Lines whose kept document is not in <paramref name="docs"/>
    /// are skipped: with nothing to keep, nothing can be called a duplicate.</summary>
    internal static IReadOnlyList<Match> Find(
        IReadOnlyList<ExpenseHoldedBooking> bookings,
        IReadOnlyList<HoldedPurchaseDocListItemDto> docs,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> contactsByUser)
    {
        var docById = docs.GroupBy(d => d.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var keptIds = bookings.Select(b => b.HoldedDocId).ToHashSet(StringComparer.Ordinal);
        var docsByContact = docs.Where(d => !string.IsNullOrEmpty(d.ContactId))
            .ToLookup(d => d.ContactId!, StringComparer.Ordinal);
        var bookingContactsByUser = bookings
            .Where(b => !string.IsNullOrEmpty(b.HoldedContactId))
            .GroupBy(b => b.CreditorUserId)
            .ToDictionary(g => g.Key, g => g.Select(b => b.HoldedContactId!).ToHashSet(StringComparer.Ordinal));

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<Match>();
        foreach (var booking in bookings)
        {
            if (!docById.TryGetValue(booking.HoldedDocId, out var kept)) continue;

            var contacts = new HashSet<string>(StringComparer.Ordinal);
            if (contactsByUser.TryGetValue(booking.CreditorUserId, out var known)) contacts.UnionWith(known);
            if (bookingContactsByUser.TryGetValue(booking.CreditorUserId, out var named)) contacts.UnionWith(named);
            if (!string.IsNullOrEmpty(kept.ContactId)) contacts.Add(kept.ContactId);

            var duplicates = contacts
                .SelectMany(c => docsByContact[c])
                .Where(d => !keptIds.Contains(d.Id) && !claimed.Contains(d.Id) && Matches(booking, d))
                .DistinctBy(d => d.Id, StringComparer.Ordinal)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
            claimed.UnionWith(duplicates.Select(d => d.Id));
            if (duplicates.Count > 0) matches.Add(new Match(booking, kept, duplicates));
        }
        return matches;
    }
}
