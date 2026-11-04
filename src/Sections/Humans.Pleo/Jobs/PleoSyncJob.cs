using Humans.Base.Interfaces;
using Humans.Pleo.Services;

namespace Humans.Pleo.Jobs;

internal sealed class PleoSyncJob(IPleoAdminService pleo, ILogger<PleoSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        switch (await pleo.SyncAsync(cancellationToken))
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
