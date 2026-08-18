using Hangfire;
using Humans.Application.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Jobs;

/// <summary>
/// Runs the Google sync outbox drain every 10 minutes via Hangfire. The queue semantics —
/// the batch pick-up, the per-event dispatch, the permanent-vs-retry classification and the
/// user GoogleEmailStatus mirror — live inside the GoogleIntegration section behind
/// <see cref="IGoogleSyncOutboxProcessor"/>; this job is the scheduler shim around it.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at the G5 jobs move
/// (nobodies-collective/Humans#866). Public and under <c>Jobs/</c> because Shell names the
/// concrete type at two sites (<c>AddScoped</c> and the recurring roll-call) and HUM0034 allows
/// a section's public types there too.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ProcessGoogleSyncOutboxJob(
    IGoogleSyncOutboxProcessor outbox,
    IHumansMetrics metrics,
    ILogger<ProcessGoogleSyncOutboxJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await outbox.ProcessQueuedAsync(cancellationToken);

            metrics.RecordJobRun("process_google_sync_outbox", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("process_google_sync_outbox", "failure");
            logger.LogError(ex, "Error processing Google sync outbox");
            throw;
        }
    }
}
