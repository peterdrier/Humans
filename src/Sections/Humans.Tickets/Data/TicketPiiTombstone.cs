namespace Humans.Tickets.Data;

/// <summary>
/// Replacement values written over buyer/attendee/receiver identifiers during
/// GDPR Article 17 erasure. Mirrors the tombstone shape the Users section leaves
/// on the User row so an erased ticket row reads the same way everywhere.
/// </summary>
internal static class TicketPiiTombstone
{
    public const string Name = "Deleted User";

    public const string EmailDomain = "@deleted.local";

    public static string EmailFor(Guid userId) => $"deleted-{userId:N}{EmailDomain}";

    /// <summary>
    /// True when a name/email pair already carries the erasure tombstone, even if
    /// <c>PiiErasedAt</c> is unset (rows erased before that column existed —
    /// nobodies-collective/Humans#1178). The email arm is the primary signal: it is a
    /// sentinel domain we mint ourselves and a vendor never returns, so a match there is
    /// conclusive. The name arm exists only to catch a row whose email was already blank.
    /// A false positive on either arm just stops one buyer's/attendee's name syncing
    /// again — the privacy-safe direction to err in.
    /// </summary>
    public static bool IsTombstoned(string? name, string? email) =>
        string.Equals(name, Name, StringComparison.Ordinal)
        || (email is not null && email.EndsWith(EmailDomain, StringComparison.OrdinalIgnoreCase));
}
