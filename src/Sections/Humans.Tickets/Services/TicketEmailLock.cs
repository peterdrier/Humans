using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.Tickets.Services;

/// <summary>An order's buyer or matched attendee address stays: the next vendor re-sync could un-match the ticket.</summary>
internal sealed class TicketEmailLock(ITicketService tickets) : IUserEmailLock
{
    public async Task<IReadOnlyCollection<string>> GetLockedAddressesAsync(Guid userId, CancellationToken ct = default)
    {
        var orders = await tickets.GetTicketOrdersAsync(ct);
        return orders
            .Where(o => o.MatchedUserId == userId && !string.IsNullOrWhiteSpace(o.BuyerEmail))
            .Select(o => o.BuyerEmail!)
            .Concat(orders
                .SelectMany(o => o.Attendees)
                .Where(a => a.MatchedUserId == userId && !string.IsNullOrWhiteSpace(a.AttendeeEmail))
                .Select(a => a.AttendeeEmail!))
            .ToList();
    }
}
