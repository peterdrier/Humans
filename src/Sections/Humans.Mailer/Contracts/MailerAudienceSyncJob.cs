using Hangfire;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Mailer.Contracts;

/// <summary>
/// Hangfire recurring job that runs <see cref="IMailerAudienceSync.SyncAllAudiencesAsync"/>
/// daily. Default cron <c>0 6 * * *</c> (06:00 UTC) — early morning, low MailerLite traffic.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-5
/// (nobodies-collective/Humans#866). It sits under <c>Contracts/</c> because Shell names the
/// concrete type at registration and HUM0034 makes every other public type in a section an
/// error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class MailerAudienceSyncJob(IMailerAudienceSync sync, ILogger<MailerAudienceSyncJob> logger)
    : IRecurringJob
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
