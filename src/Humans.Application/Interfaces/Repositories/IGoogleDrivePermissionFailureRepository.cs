using Humans.Domain.Attributes;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Google Integration section's
/// <c>google_drive_permission_failures</c> table — the only non-test file
/// that may read or write it.
/// </summary>
/// <remarks>
/// Issue nobodies-collective/Humans#945: Drive's <c>permissions.delete</c>
/// returns HTTP 403 when the permission is inherited from a parent folder,
/// which can never be deleted at the child level. Without persisted state,
/// nightly reconciliation re-lists the same permission as "extra" and
/// re-attempts the delete forever. This repository lets
/// <c>GoogleWorkspaceSyncService</c> record the terminal outcome once and
/// skip future attempts for the same (Drive file, permission) pair.
/// Registered as Singleton via <c>IDbContextFactory&lt;HumansDbContext&gt;</c>.
/// </remarks>
[Section("GoogleIntegration")]
public interface IGoogleDrivePermissionFailureRepository : IRepository
{
    /// <summary>
    /// Returns whether a terminal failure has already been recorded for this
    /// (Drive file, permission) pair. Read-only.
    /// </summary>
    Task<bool> ExistsAsync(string googleId, string permissionId, CancellationToken ct = default);

    /// <summary>
    /// Records a terminal delete failure for a (Drive file, permission) pair.
    /// No-op if a row for the pair already exists (idempotent — the nightly
    /// job may observe the same terminal condition more than once before the
    /// permission is manually resolved).
    /// </summary>
    Task RecordTerminalFailureAsync(
        string googleId,
        string permissionId,
        string email,
        string errorMessage,
        Instant detectedAt,
        CancellationToken ct = default);
}
