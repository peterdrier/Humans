namespace Humans.Tickets.Data;

/// <summary>Same tombstone shape the Users section leaves on the user row.</summary>
internal static class TicketPiiTombstone
{
    public const string Name = "Deleted User";

    public const string EmailDomain = "@deleted.local";

    public static string EmailFor(Guid userId) => $"deleted-{userId:N}{EmailDomain}";

    /// <summary>
    /// Catches rows erased before <c>PiiErasedAt</c> existed. The email arm is conclusive (a sentinel domain no vendor
    /// returns); the name arm only catches a blank email. A false positive just stops one name syncing: the safe side.
    /// </summary>
    public static bool IsTombstoned(string? name, string? email) =>
        string.Equals(name, Name, StringComparison.Ordinal)
        || (email is not null && email.EndsWith(EmailDomain, StringComparison.OrdinalIgnoreCase));
}
