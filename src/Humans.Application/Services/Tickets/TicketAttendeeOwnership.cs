using Humans.Domain.Entities;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Single source of truth for "who currently holds this issued ticket" — the
/// authority that can transfer it onward. Ownership cascades: the attendee's
/// matched user wins if set; otherwise we fall back to the order buyer.
/// Returns null when no Humans user holds it (vendor-only ticket).
/// </summary>
/// <remarks>
/// Cascade rationale: a buyer may purchase tickets for non-members (unmatched
/// attendees) and needs to manage those onward. Once a ticket gets matched to
/// a Humans user, that user takes ownership and the buyer can no longer
/// transfer it on their behalf. If sync later clears the attendee's match
/// (e.g. their email becomes unverified), the buyer regains transfer rights
/// — consistent fallback, not a special case.
/// </remarks>
public static class TicketAttendeeOwnership
{
    public static Guid? CurrentOwner(TicketAttendee attendee) =>
        attendee.MatchedUserId ?? attendee.TicketOrder?.MatchedUserId;

    public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
        CurrentOwner(attendee) == userId;
}
