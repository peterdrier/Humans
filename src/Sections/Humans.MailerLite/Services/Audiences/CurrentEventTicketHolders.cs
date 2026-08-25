using Humans.Tickets.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// The one definition of "holds a ticket to the active vendor event", shared by every
/// audience that includes or excludes ticket holders. Three copies of this pipeline meant
/// three chances for the audiences to disagree about the same set.
/// </summary>
internal static class CurrentEventTicketHolders
{
    /// <summary>
    /// User ids of Valid/CheckedIn matched attendees in the active vendor event.
    /// Buyer-only rows carry no <c>MatchedUserId</c> and so are excluded.
    /// </summary>
    public static async Task<HashSet<Guid>> ForCurrentEventAsync(
        this ITicketServiceRead tickets, CancellationToken ct)
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
