using Humans.Tickets.Domain;

namespace Humans.Tickets.Services;

/// <summary>
/// The holder is the attendee's MatchedUserId, never the order buyer: falling back to the buyer leaks tickets across
/// accounts. Unmatched attendees are owned by no one.
/// </summary>
internal static class TicketAttendeeOwnership
{
    public static Guid? CurrentOwner(TicketAttendee attendee) =>
        attendee.MatchedUserId;

    public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
        CurrentOwner(attendee) == userId;
}
