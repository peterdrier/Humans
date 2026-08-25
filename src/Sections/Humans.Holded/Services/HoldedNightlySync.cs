using Humans.Base.Attributes;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Holded.Services;

/// <summary>
/// Nightly Holded pull wrapper. It calls through to Finance for doc sync before running the local
/// ledger mirror pass.
/// </summary>
[CrossSectionWrite("Runs Finance's Holded doc sync side-effect as part of the Holded nightly pull.")]
internal sealed class HoldedNightlySync(
    IHoldedFinanceService finance,
    IHoldedService holded,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedNightlySync> logger) : IHoldedNightlySync
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        // No Holded API key (e.g. PR-preview / local dev envs) → don't call Holded; every request
        // would 401. Skip cleanly rather than fail the job each night.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogInformation("HOLDED_API_KEY_V2 not configured — skipping Holded sync.");
            return;
        }

        await finance.SyncAsync(ct);
        await holded.SyncLedgerAsync(full: false, ct);
    }
}
