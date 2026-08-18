using Humans.Application.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Write surface for the Google Integration sync outbox.
/// </summary>
public interface IGoogleSyncOutboxService : IApplicationService
{
    Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken cancellationToken = default);

    Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken cancellationToken = default);
}
