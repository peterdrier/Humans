using Humans.Domain.Attributes;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the persistent state owned by the Drive Activity monitor:
/// the per-job "last run at" marker stored in <c>system_settings</c> under a
/// dedicated key.
/// </summary>
/// <remarks>
/// <para>
/// The <c>system_settings</c> table is shared across services, but each
/// consumer owns its own key-space: this repository only reads and writes
/// <c>DriveActivityMonitor:LastRunAt</c>. Other keys remain owned by their
/// respective services (e.g. <c>EmailOutboxService</c>).
/// </para>
/// <para>
/// Anomaly audit entries are <em>not</em> persisted here; the service emits
/// them through <c>IAuditLogService.LogAsync</c>, so the only section that
/// writes <c>audit_log_entries</c> is the AuditLog section's repository
/// (design-rules section 2c / the AuditLog write boundary).
/// </para>
/// </remarks>
[Section("GoogleIntegration")]
public interface IDriveActivityMonitorRepository : IRepository
{
    /// <summary>
    /// Reads the last successful run timestamp, or <c>null</c> when no row
    /// exists (first run) or the stored string cannot be parsed as an instant
    /// (implementations log and return null in that case).
    /// </summary>
    Task<Instant?> GetLastRunTimestampAsync(CancellationToken ct = default);

    /// <summary>
    /// Advances the last-run marker when <paramref name="newLastRunAt"/> is not
    /// <c>null</c>; a <c>null</c> value is a no-op.
    /// </summary>
    /// <param name="newLastRunAt">
    /// The instant to store as <c>DriveActivityMonitor:LastRunAt</c>. When
    /// <c>null</c>, the marker is left as-is so the next run re-processes the
    /// same window; used when at least one resource failed to query.
    /// </param>
    Task AdvanceLastRunMarkerAsync(
        Instant? newLastRunAt,
        CancellationToken ct = default);
}
