using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;

namespace Humans.Consent.Jobs;

/// <summary>
/// Public because Shell names the concrete type at registration; HUM0034 makes any other public type in a
/// section assembly an error.
/// </summary>
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
