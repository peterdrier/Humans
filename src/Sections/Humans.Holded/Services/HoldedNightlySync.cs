using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Holded.Services;

/// <summary>Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section).</summary>
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
