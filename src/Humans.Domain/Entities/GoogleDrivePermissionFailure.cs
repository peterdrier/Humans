using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Records a Drive permission mutation that failed for a terminal (non-retryable)
/// reason — currently only "delete an inherited permission" (HTTP 403,
/// <c>cannotDeletePermission</c>). One row per (<see cref="GoogleId"/>,
/// <see cref="PermissionId"/>) pair. Presence of a row tells
/// <c>GoogleWorkspaceSyncService</c> to stop re-attempting the same delete on
/// future nightly reconciliation passes (nobodies-collective/Humans#945).
/// </summary>
public class GoogleDrivePermissionFailure
{
    public Guid Id { get; init; }

    /// <summary>Google Drive file/folder id the permission belongs to.</summary>
    public string GoogleId { get; set; } = string.Empty;

    /// <summary>Google Drive permission id that could not be deleted.</summary>
    public string PermissionId { get; set; } = string.Empty;

    /// <summary>The user email the permission was granted to, for diagnostics.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Google's error message at the time the terminal failure was recorded.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>When this terminal failure was first recorded.</summary>
    public Instant DetectedAt { get; init; }
}
