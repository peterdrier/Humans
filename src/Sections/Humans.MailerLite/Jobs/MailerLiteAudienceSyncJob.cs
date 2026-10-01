using Hangfire;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services;

namespace Humans.MailerLite.Jobs;

/// <summary>
/// Hangfire recurring job that runs <see cref="IMailerLiteAudienceSyncService.SyncAllAsync"/>
/// as the actor-less scheduled push.
/// Opt-in: <c>SectionJobs</c> reads <c>MailerLite:AudienceSyncCron</c>, which is unset by
/// default, so the job is contributed but not scheduled until an admin sets a cron. Until
/// then syncs happen on demand from the /MailerLite/Admin buttons.
/// </summary>
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
