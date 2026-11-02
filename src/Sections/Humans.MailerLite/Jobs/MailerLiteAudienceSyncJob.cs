using Hangfire;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services;

namespace Humans.MailerLite.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class MailerLiteAudienceSyncJob(
    IMailerLiteAudienceSyncService sync,
    IEnumerable<IMailerLiteAudience> audiences,
    ILogger<MailerLiteAudienceSyncJob> logger)
    : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        logger.LogInformation("MailerLiteAudienceSyncJob starting");
        var results = await sync.SyncAllAsync(actorUserId: null, ct);
        var failedAudiences = audiences.Select(a => a.Key)
            .Except(results.Select(r => r.Key), StringComparer.Ordinal)
            .Count();
        var errors = results.Sum(r => r.Errors);
        if (failedAudiences > 0 || errors > 0)
            throw new InvalidOperationException(
                $"MailerLite audience sync incomplete: {failedAudiences} audiences failed; {errors} subscriber operations failed.");

        logger.LogInformation(
            "MailerLiteAudienceSyncJob completed: {Count} audiences processed",
            results.Count);
    }
}
