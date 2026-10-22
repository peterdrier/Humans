using Hangfire;
using Humans.Base.Interfaces;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Finance.Jobs;

/// <summary>Holded's own <c>HoldedSyncJob</c> reads none of this. Both lock the shared "holded-api" resource so they
/// never overlap on the rate-limited API.</summary>
[DisableConcurrentExecution("holded-api", 300)]
internal sealed class HoldedDocSyncJob(
    IHoldedFinanceManagement finance,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedDocSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // No API key (PR previews, local dev): every request would 401, so skip rather than fail each night.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogInformation("HOLDED_API_KEY_V2 not configured — skipping Holded doc sync.");
            return;
        }

        // A failure records Error on the sync state and rethrows, so Hangfire shows it.
        await finance.SyncAsync(cancellationToken);
    }
}
