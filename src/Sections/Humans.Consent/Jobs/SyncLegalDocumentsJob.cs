using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;

namespace Humans.Consent.Jobs;

/// <summary>
/// Background job that syncs legal documents from the GitHub repository.
/// </summary>
/// <remarks>
/// The pass itself — pull from GitHub, fan out over the affected teams' members, mail the
/// ones who still owe a signature — lives inside the Consent section behind
/// <see cref="ILegalDocumentSyncRunner"/>: it reads <c>consent_records</c> and the legal
/// document aggregate, neither of which is visible from Base (design §15 step 6b). What is
/// left here is the schedule, the metric and the failure boundary.
///
/// It is <c>public</c> and sits under <c>Jobs/</c> because Shell names the concrete type at
/// registration and HUM0034 makes every other public type in a section assembly an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SyncLegalDocumentsJob(
    ILegalDocumentSyncRunner runner,
    IHumansMetrics metrics,
    ILogger<SyncLegalDocumentsJob> logger,
    IClock clock) : IRecurringJob
{
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
