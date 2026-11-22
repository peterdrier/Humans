using Humans.Base.Interfaces;

namespace Humans.Google.Contracts;

public interface IGoogleSyncOutboxService : IApplicationService
{
    Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken cancellationToken = default);

    Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken cancellationToken = default);
}
