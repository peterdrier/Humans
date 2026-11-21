using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// A holder's ask for a free companion on their adult ticket (B8): the light check is the ticketing team approving it,
/// which issues the companion's ticket. The companion is already in Humans; nobody gets an account made for them.
/// </summary>
internal sealed class TicketCompanionRequest
{
    public Guid Id { get; init; }

    public Guid SalesEventId { get; init; }

    /// <summary>The holder's adult ticket the companion attaches to.</summary>
    public Guid TicketId { get; init; }

    public Guid HolderUserId { get; set; }

    public Guid CompanionUserId { get; set; }

    /// <summary>The holder's short reason, read by the team only; tombstoned on erasure.</summary>
    public string Note { get; set; } = string.Empty;

    public TicketCompanionRequestState State { get; set; }

    public Instant RequestedAt { get; init; }

    public Instant? DecidedAt { get; set; }

    public Guid? DecidedByUserId { get; set; }

    /// <summary>The team's answer, shown to the holder on a decline.</summary>
    public string? DecisionNote { get; set; }

    public Guid? CompanionTicketId { get; set; }
}

internal enum TicketCompanionRequestState
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
    Withdrawn = 3,
}
