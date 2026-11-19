using Humans.Google.Contracts;
using Humans.Google.Data;

namespace Humans.Google.Services;

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
