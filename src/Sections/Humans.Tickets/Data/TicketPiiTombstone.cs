namespace Humans.Tickets.Data;

/// <summary>
/// Replacement values written over buyer/attendee/receiver identifiers during
/// GDPR Article 17 erasure. Mirrors the tombstone shape the Users section leaves
/// on the User row so an erased ticket row reads the same way everywhere.
/// </summary>
internal static class TicketPiiTombstone
{
    public const string Name = "Deleted User";

    public static string EmailFor(Guid userId) => $"deleted-{userId:N}@deleted.local";
}
