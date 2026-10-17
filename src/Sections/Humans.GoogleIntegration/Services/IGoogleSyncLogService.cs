using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Write side of the section's own record of what its syncs did to Google, plus the
/// cross-resource read behind <c>/Google/ActivityLog</c>.
/// Section-internal: every writer lives in <c>Humans.GoogleIntegration</c>, so
/// nothing about it reaches the audit log's cross-section contract
/// (nobodies-collective/Humans#1083).
/// </summary>
/// <remarks>
/// Best-effort — a failure to record is logged and swallowed so it never breaks
/// the sync it describes, matching the audit path this replaced.
/// </remarks>
internal interface IGoogleSyncLogService : IApplicationService
{
    Task LogAsync(
        GoogleSyncLogAction action,
        Guid resourceId,
        string description,
        string jobName,
        string userEmail,
        string role,
        GoogleSyncSource source,
        bool success,
        string? errorMessage = null,
        Guid? userId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Newest-first sync trail across all resources, capped at 500 rows. A null filter matches
    /// everything; <paramref name="from"/> is inclusive, <paramref name="to"/> exclusive;
    /// <paramref name="resourceName"/> is a case-insensitive contains match on the resolved name.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetRecentAsync(
        Instant? from, Instant? to, GoogleSyncLogAction? action, GoogleSyncSource? source, bool? success,
        string? resourceName, CancellationToken ct = default);
}
