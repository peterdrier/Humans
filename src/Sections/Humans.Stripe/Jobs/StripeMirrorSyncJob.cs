using Humans.Base.Interfaces;
using Humans.Stripe.Services;
using Microsoft.Extensions.Logging;

namespace Humans.Stripe.Jobs;

internal sealed class StripeMirrorSyncJob(IStripeMirrorAdminService stripe, ILogger<StripeMirrorSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        switch (await stripe.SyncAsync(cancellationToken))
        {
            // PR previews and local dev carry no key: skip rather than fail nightly.
            case StripeSyncOutcome.NotConfigured:
                logger.LogInformation("No Stripe mirror key configured — skipping Stripe mirror sync.");
                break;
            case StripeSyncOutcome.Busy:
                logger.LogWarning("Nightly Stripe sweep skipped — a sweep was already running.");
                break;
            // Each failure is already logged and on its account's sync row; failing the job makes Hangfire show it.
            case StripeSyncOutcome.SomeFailed:
                throw new InvalidOperationException("Stripe mirror sweep failed for at least one account; see /Stripe/Admin.");
        }
    }
}
