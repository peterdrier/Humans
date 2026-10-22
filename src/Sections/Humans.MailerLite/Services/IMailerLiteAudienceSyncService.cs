using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services.Dtos;

namespace Humans.MailerLite.Services;

internal interface IMailerLiteAudienceSyncService : IApplicationService
{
    /// <summary>One pass: the MailerLite snapshot and the sync states are each read once.</summary>
    Task<IReadOnlyList<AudienceStats>> ComputeAllStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes the summary audit entry. A null <paramref name="actorUserId"/> (the scheduled job) uses the
    /// job-actor overload.
    /// </summary>
    [ExternalWrite]
    Task<AudienceSyncResult> SyncAsync(
        IMailerLiteAudience audience, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>One audience at a time.</summary>
    [ExternalWrite]
    Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(
        Guid? actorUserId = null, CancellationToken ct = default);
}
