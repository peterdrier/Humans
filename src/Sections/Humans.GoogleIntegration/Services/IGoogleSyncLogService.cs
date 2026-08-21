using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Write side of the section's own record of what its syncs did to Google.
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
}
