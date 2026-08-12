using Hangfire;
using Humans.Mailer.Contracts;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that runs <see cref="IMailerAudienceSync.SyncAllAudiencesAsync"/>
/// daily. Default cron <c>0 6 * * *</c> (06:00 UTC) — early morning, low MailerLite traffic.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class MailerAudienceSyncJob(IMailerAudienceSync sync, ILogger<MailerAudienceSyncJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        logger.LogInformation("MailerAudienceSyncJob starting");
        var count = await sync.SyncAllAudiencesAsync(ct);
        logger.LogInformation(
            "MailerAudienceSyncJob completed: {Count} audiences processed",
            count);
    }
}
