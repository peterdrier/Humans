using Humans.Tickets.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>The one definition of "holds a ticket to the active vendor event", so audiences can't disagree on it.</summary>
internal static class CurrentEventTicketHolders
{
    /// <summary>Valid/CheckedIn matched attendees. Buyer-only rows have no <c>MatchedUserId</c>, so they're excluded.</summary>
    public static async Task<HashSet<Guid>> ForCurrentEventAsync(
        ITicketService tickets, CancellationToken ct)
    {
        var orders = await tickets.GetTicketOrdersAsync(ct);
        return orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .Where(a => a.MatchedUserId.HasValue
                && a.Status is TicketAttendeeStatus.Valid or TicketAttendeeStatus.CheckedIn)
            .Select(a => a.MatchedUserId!.Value)
            .ToHashSet();
    }
}
