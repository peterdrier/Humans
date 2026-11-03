using System.Runtime.ExceptionServices;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Holded.Services;

internal sealed class HoldedNightlySync(
    IHoldedService holded,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedNightlySync> logger) : IHoldedNightlySync
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        // No key (PR previews, local dev): every request would 401, so skip rather than fail nightly.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogInformation("HOLDED_API_KEY_V2 not configured — skipping Holded sync.");
            return;
        }

        // The gates are non-blocking: if a sync is already running this one returns false. Say so, or a skipped
        // night reads as a successful sync in the job history.
        // A failed document pull (already logged and recorded as Error) must not cost the ledger its nightly sweep;
        // it is rethrown afterwards so the job still shows as failed.
        ExceptionDispatchInfo? docsFailure = null;
        try
        {
            if (!await holded.SyncPurchaseDocumentsAsync(ct))
                logger.LogWarning("Nightly Holded purchase-document sync skipped — a sync was already running.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            docsFailure = ExceptionDispatchInfo.Capture(ex);
        }

        if (!await holded.SyncLedgerAsync(full: false, ct))
            logger.LogWarning("Nightly Holded ledger sweep skipped — a sweep was already running.");
        docsFailure?.Throw();
    }
}
