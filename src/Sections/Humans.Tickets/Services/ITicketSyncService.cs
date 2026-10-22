using NodaTime;
using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

internal interface ITicketSyncService : ITicketSync, IApplicationService
{
    Task ResetSyncStateForFullResyncAsync();

    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}
