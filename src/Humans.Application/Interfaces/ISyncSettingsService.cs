using System.Security.Claims;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Manages per-service sync mode settings.
/// </summary>
public interface ISyncSettingsService
{
    /// <summary>Get all sync settings.</summary>
    Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Get sync mode for a specific service.</summary>
    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    /// <summary>
    /// Update sync mode for a service. Authorization is enforced at the service boundary —
    /// callers must pass a <see cref="ClaimsPrincipal"/> with Admin (or system) privileges.
    /// </summary>
    Task UpdateModeAsync(
        SyncServiceType serviceType,
        SyncMode mode,
        Guid actorUserId,
        ClaimsPrincipal principal,
        CancellationToken ct = default);
}
