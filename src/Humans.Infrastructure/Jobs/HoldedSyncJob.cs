using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Jobs;

/// <summary>Nightly Holded pull: purchase docs → budget-category actuals, plus the creditor daybook ledger.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(
    IHoldedFinanceService finance,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // No Holded API key (e.g. PR-preview / local dev envs) → don't call Holded; every request
        // would 401. Skip cleanly rather than fail the job each night.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogInformation("HOLDED_API_KEY not configured — skipping Holded sync.");
            return;
        }

        await finance.SyncAsync(cancellationToken);
        await finance.SyncCreditorLedgerAsync(cancellationToken);
    }
}
