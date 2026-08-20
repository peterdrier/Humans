using Humans.Base.Enums;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Read side of the section's Google sync trail, for the pages that render it.
/// </summary>
/// <remarks>
/// This project's <c>Contracts/</c> folder, not the <c>Humans.GoogleIntegration.Contracts</c>
/// leaf: the only out-of-section consumer is Monitor's <c>SyncAudit</c> page, which already
/// references <c>Humans.GoogleIntegration</c> for the <c>&lt;vc:google-sync-log&gt;</c> tag
/// helper. Public only because <c>GoogleSyncLogViewComponent</c> is — the write path
/// (<c>IGoogleSyncLogService</c>) stays internal. Same arrangement AuditLog uses for
/// <c>IAuditViewerService</c>.
/// </remarks>
public interface IGoogleSyncLogViewer : IApplicationService
{
    /// <summary>Sync trail for one Google resource, newest first.</summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>Sync trail for one human, newest first, following merge tombstones.</summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetForUserAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Rendered shape of a <c>google_sync_log</c> row, with the resource name resolved.</summary>
public sealed record GoogleSyncLogView(
    GoogleSyncLogAction Action,
    Instant OccurredAt,
    string Description,
    string? ResourceName,
    string UserEmail,
    string Role,
    GoogleSyncSource Source,
    bool Success,
    string? ErrorMessage);

/// <summary>What a Google sync did to a resource.</summary>
public enum GoogleSyncLogAction
{
    AccessGranted,
    AccessRevoked
}
