namespace Humans.Tickets.Domain;

/// <summary>Always one row, Id 1. Edited on /Settings#tickets.</summary>
internal sealed class TicketSettings
{
    public int Id { get; init; }

    /// <summary>Off between sales seasons; the admin "Sync now" and full resync buttons run either way.</summary>
    public bool IsScheduledSyncEnabled { get; set; }
}
