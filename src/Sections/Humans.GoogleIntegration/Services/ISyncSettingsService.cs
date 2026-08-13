using Humans.Domain.Enums;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Manages per-service sync mode settings.
/// </summary>
internal interface ISyncSettingsService : IApplicationService
{
    /// <summary>Get all sync settings.</summary>
    Task<IReadOnlyList<SyncServiceSettingsInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Get sync mode for a specific service.</summary>
    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    /// <summary>Update sync mode for a service.</summary>
    Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default);
}

internal sealed record SyncServiceSettingsInfo(
    Guid Id,
    SyncServiceType ServiceType,
    SyncMode SyncMode,
    Instant UpdatedAt,
    Guid? UpdatedByUserId);
