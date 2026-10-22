using Humans.Expenses.Contracts;
using Humans.Holded.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// Same description and booked total on another document of the member's contacts. Notes are not in this rule: the
/// v2 purchase list never returns them, so a delete confirms them with a GET first. A document some line links to is
/// never a duplicate, and an extra document matching several identical lines counts once.
/// </summary>
internal static class HoldedDuplicateMatcher
{
    internal sealed record Match(
        ExpenseHoldedBooking Booking,
        HoldedPurchaseDocListItemDto Kept,
        IReadOnlyList<HoldedPurchaseDocListItemDto> Duplicates);

    /// <summary>The list-level rule. A false positive only refuses a payout; a delete needs the notes confirmed
    /// first.</summary>
    internal static bool Matches(ExpenseHoldedBooking booking, HoldedPurchaseDocListItemDto doc) =>
        string.Equals(doc.Description, booking.Description, StringComparison.Ordinal)
        && doc.Total == booking.BookedAmount;

    /// <summary><paramref name="contactsByUser"/> widens the search to every contact the member holds. Lines whose
    /// kept document is not in <paramref name="docs"/> are skipped: nothing to keep, nothing duplicated.</summary>
    internal static IReadOnlyList<Match> Find(
        IReadOnlyList<ExpenseHoldedBooking> bookings,
        IReadOnlyList<HoldedPurchaseDocListItemDto> docs,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> contactsByUser) =>
        Find(bookings, docs, contactsByUser, Matches);

    /// <summary>With <paramref name="isDuplicate"/> in place of the list-level rule: once notes are known, a candidate
    /// goes to the line whose notes it carries, not the first line with the same description and total.</summary>
    internal static IReadOnlyList<Match> Find(
        IReadOnlyList<ExpenseHoldedBooking> bookings,
        IReadOnlyList<HoldedPurchaseDocListItemDto> docs,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> contactsByUser,
        Func<ExpenseHoldedBooking, HoldedPurchaseDocListItemDto, bool> isDuplicate)
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
                .Where(d => !keptIds.Contains(d.Id) && !claimed.Contains(d.Id) && isDuplicate(booking, d))
                .DistinctBy(d => d.Id, StringComparer.Ordinal)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
            claimed.UnionWith(duplicates.Select(d => d.Id));
            if (duplicates.Count > 0) matches.Add(new Match(booking, kept, duplicates));
        }
        return matches;
    }
}
