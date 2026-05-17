using Humans.Domain.Entities;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Current ticket holder = attendee.MatchedUserId else order.MatchedUserId; null if vendor-only.
/// </summary>
public static class TicketAttendeeOwnership
{
    public static Guid? CurrentOwner(TicketAttendee attendee) =>
        attendee.MatchedUserId ?? attendee.TicketOrder?.MatchedUserId;

    public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
        CurrentOwner(attendee) == userId;
}
