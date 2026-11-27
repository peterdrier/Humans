using Humans.Base.Interfaces;
using Humans.Pleo.Services;

namespace Humans.Pleo.Jobs;

internal sealed class PleoSyncJob(IPleoAdminService pleo, ILogger<PleoSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        PleoSyncOutcome outcome;
        try
        {
            outcome = await pleo.SyncAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Nightly Pleo sweep interrupted by shutdown");
            throw;
        }
        catch (PleoPermanentException ex)
        {
            // A 4xx is the key's limit, so Hangfire's retries would only repeat it; the sync row already holds the error.
            logger.LogWarning("Nightly Pleo sweep failed permanently; not retrying: {Reason}", ex.Message);
            return;
        }

        switch (outcome)
        {
            // PR previews and local dev carry no key: skip rather than fail nightly.
            // SyncAsync logs which setting is missing.
            case PleoSyncOutcome.NotConfigured:
                break;
            case PleoSyncOutcome.Busy:
                logger.LogWarning("Nightly Pleo sweep skipped — a sweep was already running.");
                break;
        }
    }
}
