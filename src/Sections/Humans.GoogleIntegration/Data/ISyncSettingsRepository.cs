using Humans.GoogleIntegration.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.GoogleIntegration.Data;

internal interface ISyncSettingsRepository : IRepository
{
    Task<IReadOnlyList<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default);

    /// <summary><see cref="SyncMode.None"/> when no row exists. Hot path: every gateway op reads it.</summary>
    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    /// <summary>Null when the service has no row: a configuration error callers must surface.</summary>
    Task<SyncServiceSettings?> UpdateModeAsync(
        SyncServiceType serviceType,
        SyncMode mode,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
