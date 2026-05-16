using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Manages per-service sync mode settings.
/// </summary>
public interface ISyncSettingsService : IApplicationService
{
    /// <summary>Get all sync settings.</summary>
    Task<IReadOnlyList<SyncServiceSettingsInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Get sync mode for a specific service.</summary>
    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    /// <summary>Update sync mode for a service.</summary>
    Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default);
}

public sealed record SyncServiceSettingsInfo(
    Guid Id,
    SyncServiceType ServiceType,
    SyncMode SyncMode,
    Instant UpdatedAt,
    Guid? UpdatedByUserId);
