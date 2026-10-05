using Humans.Tickets.Domain;

namespace Humans.Tickets.Services;

/// <summary>
/// Current ticket holder = attendee.MatchedUserId; null if unmatched or vendor-only.
/// The order buyer never owns the attendee (nobodies-collective/Humans#856): falling back
/// to the order's MatchedUserId leaks tickets across accounts. Unmatched attendees are
/// owned by no one until matched.
/// </summary>
internal static class TicketAttendeeOwnership
{
    public static Guid? CurrentOwner(TicketAttendee attendee) =>
        attendee.MatchedUserId;

    public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
        CurrentOwner(attendee) == userId;
}
