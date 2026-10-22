using Hangfire;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services;

namespace Humans.MailerLite.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class MailerLiteAudienceSyncJob(IMailerLiteAudienceSyncService sync, ILogger<MailerLiteAudienceSyncJob> logger)
    : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        logger.LogInformation("MailerLiteAudienceSyncJob starting");
        var results = await sync.SyncAllAsync(actorUserId: null, ct);
        logger.LogInformation(
            "MailerLiteAudienceSyncJob completed: {Count} audiences processed",
            results.Count);
    }
}
