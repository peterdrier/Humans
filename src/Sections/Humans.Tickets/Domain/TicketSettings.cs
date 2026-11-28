namespace Humans.Tickets.Domain;

/// <summary>Always one row, Id 1. Edited on /Settings#tickets.</summary>
internal sealed class TicketSettings
{
    public int Id { get; init; }

    /// <summary>Off between sales seasons; the admin "Sync now" and full resync buttons run either way.</summary>
    public bool IsScheduledSyncEnabled { get; set; }

    /// <summary>
    /// Days after an event's strike ends that a ticket holder's deletion waits, and after which the platform erases the
    /// event's personal data; null means <see cref="DefaultPostEventHoldDays"/>.
    /// </summary>
    public int? PostEventHoldDays { get; set; }

    public const int DefaultPostEventHoldDays = 30;
}
