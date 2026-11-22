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

        // A failed phase must not cost the later phases their sweep; report every failure afterwards.
        var failures = new List<Exception>();
        try
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await holded.SyncPurchaseDocumentsAsync(ct))
                    logger.LogWarning("Nightly Holded purchase-document sync skipped — a sync was already running.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add(ex);
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await holded.SyncContactsAsync(ct))
                    logger.LogWarning("Nightly Holded contact sync skipped — a sync was already running.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add(ex);
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await holded.SyncLedgerAsync(full: false, ct))
                    logger.LogWarning("Nightly Holded ledger sweep skipped — a sweep was already running.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add(ex);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (failures.Count > 0)
                throw new AggregateException("Nightly Holded sync failed before shutdown.", failures.Append(ex));
            logger.LogWarning("Nightly Holded sync interrupted by shutdown");
            throw;
        }

        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Nightly Holded sync failed in multiple phases.", failures);
    }
}
