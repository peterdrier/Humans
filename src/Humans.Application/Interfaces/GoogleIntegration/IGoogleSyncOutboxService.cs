using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.GoogleIntegration;

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
