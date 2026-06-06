using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;

namespace Humans.Application.Services.GoogleIntegration;

public sealed class GoogleSyncOutboxService(IGoogleSyncOutboxRepository repository)
    : IGoogleSyncOutboxService
{
    public Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken cancellationToken = default) =>
        repository.AddAsync(outboxEvent, cancellationToken);

    public Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken cancellationToken = default) =>
        repository.AddRangeAsync(outboxEvents, cancellationToken);
}
