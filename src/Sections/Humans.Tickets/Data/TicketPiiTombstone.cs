namespace Humans.Tickets.Data;

/// <summary>Same tombstone shape the Users section leaves on the user row.</summary>
internal static class TicketPiiTombstone
{
    public const string Name = "Deleted User";

    public const string EmailDomain = "@deleted.local";

    public static string EmailFor(Guid userId) => $"deleted-{userId:N}{EmailDomain}";

    /// <summary>
    /// Catches legacy erased rows even without <c>PiiErasedAt</c>. The email sentinel marks erased contact details;
    /// the name sentinel also covers unaddressed dependents.
    /// </summary>
    public static bool IsTombstoned(string? name, string? email) =>
        string.Equals(name, Name, StringComparison.Ordinal)
        || (email is not null && email.EndsWith(EmailDomain, StringComparison.OrdinalIgnoreCase));
}
