using Hangfire;
using Humans.Base.Interfaces;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Finance.Jobs;

/// <summary>Nightly pull of Holded purchase docs into Finance's mirror (budget-category actuals,
/// the unmatched queue). The schedule lives in <c>SectionJobs.cs</c>; the ledger mirror's own
/// nightly sweep is Holded's <c>HoldedSyncJob</c>, which reads none of this.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class HoldedDocSyncJob(
    IHoldedFinanceManagement finance,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedDocSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // No Holded API key (e.g. PR-preview / local dev envs) → don't call Holded; every request
        // would 401. Skip cleanly rather than fail the job each night.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogInformation("HOLDED_API_KEY_V2 not configured — skipping Holded doc sync.");
            return;
        }

        // A failure records Error on the doc-sync state and rethrows, so Hangfire shows it.
        await finance.SyncAsync(cancellationToken);
    }
}
