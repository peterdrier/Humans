using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Consent.Services;

namespace Humans.Consent.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class SyncLegalDocumentsJob(
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
