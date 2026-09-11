using Hangfire;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Jobs;

/// <summary>
/// Runs the Google sync outbox drain every 10 minutes via Hangfire. The queue semantics —
/// the batch pick-up, the per-event dispatch, the permanent-vs-retry classification and the
/// user GoogleEmailStatus mirror — live inside the GoogleIntegration section behind
/// <see cref="IGoogleSyncOutboxProcessor"/>; this job is the scheduler shim around it.
/// </summary>
/// <remarks>
/// Public because the concrete type is named at two sites (<c>AddScoped</c> in
/// <c>Section.cs</c>, the descriptor in <c>SectionJobs.cs</c>); HUM0034 allows a section's
/// public job types.
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
