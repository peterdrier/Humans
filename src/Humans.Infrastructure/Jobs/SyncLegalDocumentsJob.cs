using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Consent.Contracts;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs legal documents from the GitHub repository.
/// </summary>
/// <remarks>
/// The pass itself — pull from GitHub, fan out over the affected teams' members, mail the
/// ones who still owe a signature — lives inside the Consent section behind
/// <see cref="ILegalDocumentSyncRunner"/>: it reads <c>consent_records</c> and the legal
/// document aggregate, neither of which is visible from Base since the section's G5 move
/// (design §15 step 6b). What is left here is the schedule, the metric and the
/// failure boundary.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SyncLegalDocumentsJob(
    ILegalDocumentSyncRunner runner,
    IHumansMetrics metrics,
    ILogger<SyncLegalDocumentsJob> logger,
    IClock clock) : IRecurringJob
{
    /// <summary>
    /// Executes the legal document sync job.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting legal document sync at {Time}", clock.GetCurrentInstant());

        try
        {
            await runner.SyncAndNotifyAsync(cancellationToken);

            metrics.RecordJobRun("sync_legal_documents", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("sync_legal_documents", "failure");
            logger.LogError(ex, "Error syncing legal documents");
            throw;
        }
    }
}
