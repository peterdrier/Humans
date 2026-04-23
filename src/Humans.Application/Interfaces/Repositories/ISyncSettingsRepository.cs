using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Google Integration section's <c>sync_service_settings</c>
/// table. The only non-test file that may touch that DbSet after the Google
/// Integration §15 migration lands.
/// </summary>
/// <remarks>
/// Returned entities never include the cross-domain <c>UpdatedByUser</c>
/// navigation — callers resolve display names via <c>IUserService</c>. See
/// <c>docs/architecture/design-rules.md</c> §3 and §6 for the canonical shape.
/// </remarks>
public interface ISyncSettingsRepository
{
    /// <summary>
    /// Returns every sync service settings row, ordered by <see cref="SyncServiceType"/>.
    /// Read-only (<c>AsNoTracking</c>). The <c>UpdatedByUser</c> navigation is
    /// <b>not</b> loaded — callers resolve the display name via
    /// <c>IUserService.GetByIdAsync(UpdatedByUserId)</c>.
    /// </summary>
    Task<IReadOnlyList<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current <see cref="SyncMode"/> for the given service, or
    /// <see cref="SyncMode.None"/> when no row exists. Read-only (AsNoTracking).
    /// Called on every gateway op by the Google sync services, so this is the hot path.
    /// </summary>
    Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default);

    /// <summary>
    /// Atomically updates the sync mode for <paramref name="serviceType"/> and
    /// records the actor and timestamp. Returns <c>false</c> when no row exists
    /// for the given service — callers should treat this as a configuration
    /// error and surface it rather than silently succeeding.
    /// </summary>
    Task<bool> UpdateModeAsync(
        SyncServiceType serviceType,
        SyncMode mode,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
