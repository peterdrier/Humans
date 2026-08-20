using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services.Dtos;

namespace Humans.MailerLite.Services;

/// <summary>
/// Orchestrates pulling audience definitions, diffing against ML state, and
/// pushing membership changes to MailerLite. Stat-only reads are split out so
/// the dashboard can render without forcing a sync.
/// </summary>
internal interface IMailerLiteAudienceSyncService : IApplicationService
{
    /// <summary>Read-only stats for one audience: candidates / excluded-unsubscribed / currently-in-group.</summary>
    Task<AudienceStats> ComputeStatsAsync(IMailerLiteAudience audience, CancellationToken ct = default);

    /// <summary>
    /// Read-only stats for every registered audience in a single pass.
    /// Pulls the MailerLite subscriber/group snapshot once and the audit-log
    /// last-sync entries once, then folds them into per-audience rows. Used
    /// by the /MailerLite/Admin dashboard so the controller doesn't fan out
    /// multiple service+audit calls per render.
    /// </summary>
    Task<IReadOnlyList<AudienceStats>> ComputeAllStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Build diff and apply to MailerLite. Writes the summary audit entry.
    /// Pass <paramref name="actorUserId"/> for human-triggered runs (admin
    /// "Push Now"); leave null for the scheduled job so the audit entry uses
    /// the job-actor overload.
    /// </summary>
    [ExternalWrite]
    Task<AudienceSyncResult> SyncAsync(
        IMailerLiteAudience audience, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Calls SyncAsync sequentially for every registered audience.</summary>
    [ExternalWrite]
    Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(
        Guid? actorUserId = null, CancellationToken ct = default);
}
