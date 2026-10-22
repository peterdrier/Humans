using Humans.GoogleIntegration.Contracts;
using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Services;

internal interface ISyncSettingsService : IApplicationService
{
    Task<IReadOnlyList<SyncServiceSettingsInfo>> GetAllAsync(CancellationToken ct = default);

    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default);
}

internal sealed record SyncServiceSettingsInfo(
    Guid Id,
    SyncServiceType ServiceType,
    SyncMode SyncMode,
    Instant UpdatedAt,
    Guid? UpdatedByUserId);
