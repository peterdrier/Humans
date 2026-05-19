using Hangfire;
using Humans.Application.Interfaces.Mailer;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that runs <see cref="IMailerAudienceSyncService.SyncAllAsync"/>
/// daily. Default cron <c>0 6 * * *</c> (06:00 UTC) — early morning, low MailerLite traffic.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class MailerAudienceSyncJob(IMailerAudienceSyncService sync, ILogger<MailerAudienceSyncJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        logger.LogInformation("MailerAudienceSyncJob starting");
        var results = await sync.SyncAllAsync(actorUserId: null, ct);
        logger.LogInformation(
            "MailerAudienceSyncJob completed: {Count} audiences processed",
            results.Count);
    }
}
