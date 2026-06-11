using Humans.Domain.Entities;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Current ticket holder = attendee.MatchedUserId; null if unmatched or vendor-only.
/// The legacy buyer-fallback (attendee.TicketOrder?.MatchedUserId) was removed in
/// nobodies-collective/Humans#856: every attendee matched via AttendeeContactImportService
/// has its own MatchedUserId, so the order-buyer arm only leaked cross-account tickets.
/// Unmatched attendees (MatchedUserId == null) are not owned by anyone until matched.
/// </summary>
public static class TicketAttendeeOwnership
{
    public static Guid? CurrentOwner(TicketAttendee attendee) =>
        attendee.MatchedUserId;

    public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
        CurrentOwner(attendee) == userId;
}
