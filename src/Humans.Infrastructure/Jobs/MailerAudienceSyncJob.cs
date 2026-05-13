using Hangfire;
using Humans.Application.Interfaces.Mailer;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that runs <see cref="IMailerAudienceSyncService.SyncAllAsync"/>
/// daily. Default cron <c>0 6 * * *</c> (06:00 UTC) — early morning, low MailerLite traffic.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class MailerAudienceSyncJob
{
    private readonly IMailerAudienceSyncService _sync;
    private readonly ILogger<MailerAudienceSyncJob> _logger;

    public MailerAudienceSyncJob(IMailerAudienceSyncService sync, ILogger<MailerAudienceSyncJob> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("MailerAudienceSyncJob starting");
        var results = await _sync.SyncAllAsync(ct);
        _logger.LogInformation(
            "MailerAudienceSyncJob completed: {Count} audiences processed",
            results.Count);
    }
}
