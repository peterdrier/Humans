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
        catch (PleoPermanentException ex)
        {
            // A 4xx is the key's limit, so Hangfire's retries would only repeat it; the sync row already holds the error.
            logger.LogWarning(ex, "Nightly Pleo sweep failed permanently; not retrying.");
            return;
        }

        switch (outcome)
        {
            // PR previews and local dev carry no key: skip rather than fail nightly.
            case PleoSyncOutcome.NotConfigured:
                logger.LogInformation("PLEO_API_KEY or PLEO_COMPANY_ID not configured — skipping Pleo sync.");
                break;
            case PleoSyncOutcome.Busy:
                logger.LogWarning("Nightly Pleo sweep skipped — a sweep was already running.");
                break;
        }
    }
}
