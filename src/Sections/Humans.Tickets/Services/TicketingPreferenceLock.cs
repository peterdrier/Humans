using Humans.Email.Contracts;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

/// <summary>A matched ticket holder keeps Ticketing mail: it carries their event information.</summary>
internal sealed class TicketingPreferenceLock(ITicketService tickets) : ICommunicationPreferenceLock
{
    public async Task<IReadOnlyCollection<MessageCategory>> GetLockedCategoriesAsync(
        Guid userId, CancellationToken ct = default) =>
        (await tickets.GetUserTicketHoldingsAsync(userId, ct)).HasTicketAttendeeMatch
            ? [MessageCategory.Ticketing]
            : [];
}
