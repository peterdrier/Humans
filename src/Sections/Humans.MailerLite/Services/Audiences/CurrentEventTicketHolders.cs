using Humans.Tickets.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// The one definition of "holds a ticket to the active vendor event", shared by every
/// audience that includes or excludes ticket holders. Three copies of this pipeline meant
/// three chances for the audiences to disagree about the same set.
/// </summary>
/// <remarks>
/// A plain static over <see cref="ITicketServiceRead"/>, deliberately not an extension
/// method: this is a MailerLite-side composition of one Tickets read, and dressing it up as
/// a method on that interface would both fragment its surface and contradict its own
/// surface budget (memory/code/no-extensions-for-owned-classes.md).
/// </remarks>
internal static class CurrentEventTicketHolders
{
    /// <summary>
    /// User ids of Valid/CheckedIn matched attendees in the active vendor event.
    /// Buyer-only rows carry no <c>MatchedUserId</c> and so are excluded.
    /// </summary>
    public static async Task<HashSet<Guid>> ForCurrentEventAsync(
        ITicketServiceRead tickets, CancellationToken ct)
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
