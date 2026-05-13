using Humans.Application.Interfaces.Mailer.Dtos;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// Orchestrates pulling audience definitions, diffing against ML state, and
/// pushing membership changes to MailerLite. Stat-only reads are split out so
/// the dashboard can render without forcing a sync.
/// </summary>
public interface IMailerAudienceSyncService : IApplicationService
{
    /// <summary>Read-only: candidates / excluded-unsubscribed / currently-in-group / last sync.</summary>
    Task<AudienceStats> ComputeStatsAsync(IMailerAudience audience, CancellationToken ct = default);

    /// <summary>Build diff and apply to MailerLite. Writes the summary audit entry.</summary>
    Task<AudienceSyncResult> SyncAsync(IMailerAudience audience, CancellationToken ct = default);

    /// <summary>Calls SyncAsync sequentially for every registered audience.</summary>
    Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(CancellationToken ct = default);
}
