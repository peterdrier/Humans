using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Holded.Services;

/// <summary>
/// Nightly ledger-mirror sweep. Finance's purchase-doc sync is Finance's own job
/// (<c>HoldedDocSyncJob</c>); the sweep reads none of its output.
/// </summary>
internal sealed class HoldedNightlySync(
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

        // The sweep gate is non-blocking: a sweep already in flight (an admin's Sync now, or a
        // full resync still rebuilding) makes this one return false rather than queue. Tonight's
        // window then goes unswept, so say so — reconciliation will cover it, but a silent no-op
        // reads as a successful sync in the job history.
        if (!await holded.SyncLedgerAsync(full: false, ct))
            logger.LogWarning("Nightly Holded ledger sweep skipped — a sweep was already running.");
    }
}
