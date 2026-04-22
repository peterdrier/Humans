using NodaTime;
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the persistent state owned by the Drive Activity monitor:
/// the per-job "last run at" marker (stored in <c>system_settings</c> under a
/// dedicated key), the anomaly audit-log entries the monitor emits, and the
/// fallback lookup from a Google-OAuth <c>people/{id}</c> to a local user's
/// email via the ASP.NET Identity login tables.
/// </summary>
/// <remarks>
/// <para>
/// The <c>system_settings</c> table is shared across services, but each
/// consumer owns its own key-space: this repository only reads and writes
/// <c>DriveActivityMonitor:LastRunAt</c>. Other keys remain owned by their
/// respective services (e.g. <c>EmailOutboxService</c>).
/// </para>
/// <para>
/// Anomaly audit entries are persisted here rather than through
/// <c>IAuditLogService</c> so the write is atomic with the marker advance —
/// the pre-§15 implementation relied on a shared Scoped <c>DbContext</c> to
/// get that guarantee. <c>IAuditLogService</c> will migrate to the §15 pattern
/// under issue #552; at that point this method can delegate to it.
/// </para>
/// <para>
/// The Identity login / user read is a cross-section fallback used only when
/// the Directory API can't resolve a <c>people/{id}</c>. It returns the
/// <c>User.Email</c> (OAuth primary email) directly rather than going through
/// <c>IUserService</c> so the query stays a single join — matching the
/// pre-migration behavior.
/// </para>
/// </remarks>
public interface IDriveActivityMonitorRepository
{
    /// <summary>
    /// Reads the last successful run timestamp, or <c>null</c> when no row
    /// exists (first run) or the stored string cannot be parsed as an instant
    /// (implementations log and return null in that case).
    /// </summary>
    Task<Instant?> GetLastRunTimestampAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the anomaly audit entries detected during this run, and — when
    /// <paramref name="newLastRunAt"/> is not <c>null</c> — advances the
    /// last-run marker. Everything commits in a single transaction so audit
    /// entries and the marker never diverge.
    /// </summary>
    /// <param name="anomalies">
    /// Fully-populated <see cref="AuditLogEntry"/> rows with their <c>Id</c>
    /// and <c>OccurredAt</c> already set by the caller. The repository only
    /// adds them to the context and saves.
    /// </param>
    /// <param name="newLastRunAt">
    /// The instant to store as <c>DriveActivityMonitor:LastRunAt</c>. When
    /// <c>null</c>, the marker is left as-is so the next run re-processes the
    /// same window — used when at least one resource failed to query.
    /// </param>
    Task PersistAnomaliesAsync(
        IReadOnlyList<AuditLogEntry> anomalies,
        Instant? newLastRunAt,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a Google-OAuth provider-key to the local user's <c>User.Email</c>
    /// address, or <c>null</c> when no matching login or user exists.
    /// Used as a last-resort fallback when the Directory API cannot resolve a
    /// <c>people/{id}</c> actor (for example, deleted Workspace accounts whose
    /// local user row still exists).
    /// </summary>
    Task<string?> TryResolveEmailByGoogleUserIdAsync(
        string googleUserId, CancellationToken ct = default);
}
