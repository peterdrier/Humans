using Humans.GoogleIntegration.Contracts;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Application.Interfaces;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

internal sealed class GoogleSyncOutboxService(IGoogleSyncOutboxRepository repository)
    : IGoogleSyncOutboxService
{
    public Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken cancellationToken = default) =>
        repository.AddAsync(outboxEvent, cancellationToken);

    public Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken cancellationToken = default) =>
        repository.AddRangeAsync(outboxEvents, cancellationToken);
}
