using Hangfire;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Jobs;

/// <summary>
/// Scheduler shim around <see cref="IGoogleSyncOutboxProcessor"/>. Public because Section.cs
/// name it (HUM0034 allows public job types).
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ProcessGoogleSyncOutboxJob(
    IGoogleSyncOutboxProcessor outbox,
    IGoogleDriveActivityClient googleClient,
    IHumansMetrics metrics,
    ILogger<ProcessGoogleSyncOutboxJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await outbox.ProcessQueuedAsync(cancellationToken);

            metrics.RecordJobRun(
                "process_google_sync_outbox",
                googleClient.IsConfigured ? "success" : "skipped");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("process_google_sync_outbox", "failure");
            logger.LogError(ex, "Error processing Google sync outbox");
            throw;
        }
    }
}
