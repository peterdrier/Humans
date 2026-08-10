using Hangfire;
using Humans.Application.Interfaces;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Jobs;

/// <summary>Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section).</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(
    IHoldedFinanceService finance,
    IHoldedService holded,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // No Holded API key (e.g. PR-preview / local dev envs) → don't call Holded; every request
        // would 401. Skip cleanly rather than fail the job each night.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogInformation("HOLDED_API_KEY_V2 not configured — skipping Holded sync.");
            return;
        }

        await finance.SyncAsync(cancellationToken);
        await holded.SyncLedgerAsync(full: false, cancellationToken);
    }
}
