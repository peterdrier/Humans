using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Manages per-service sync mode settings.
/// </summary>
public interface ISyncSettingsService
{
    /// <summary>Get all sync settings.</summary>
    Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Get sync mode for a specific service.</summary>
    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    /// <summary>Update sync mode for a service.</summary>
    Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default);
}
