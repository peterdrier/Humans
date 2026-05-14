using Humans.Application.Interfaces.Mailer.Dtos;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// Orchestrates pulling audience definitions, diffing against ML state, and
/// pushing membership changes to MailerLite. Stat-only reads are split out so
/// the dashboard can render without forcing a sync.
/// </summary>
public interface IMailerAudienceSyncService : IApplicationService
{
    /// <summary>Read-only stats for one audience: candidates / excluded-unsubscribed / currently-in-group.</summary>
    Task<AudienceStats> ComputeStatsAsync(IMailerAudience audience, CancellationToken ct = default);

    /// <summary>
    /// Read-only stats for every registered audience in a single pass.
    /// Pulls the MailerLite subscriber/group snapshot once and the audit-log
    /// last-sync entries once, then folds them into per-audience rows. Used
    /// by the /Mailer/Admin dashboard so the controller doesn't fan out
    /// multiple service+audit calls per render.
    /// </summary>
    Task<IReadOnlyList<AudienceStats>> ComputeAllStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Build diff and apply to MailerLite. Writes the summary audit entry.
    /// Pass <paramref name="actorUserId"/> for human-triggered runs (admin
    /// "Push Now"); leave null for the scheduled job so the audit entry uses
    /// the job-actor overload.
    /// </summary>
    Task<AudienceSyncResult> SyncAsync(
        IMailerAudience audience, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Calls SyncAsync sequentially for every registered audience.</summary>
    Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(
        Guid? actorUserId = null, CancellationToken ct = default);
}
