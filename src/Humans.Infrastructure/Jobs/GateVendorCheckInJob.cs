using System.Net.Http;
using Hangfire;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Best-effort mirror of a gate admit to the ticket vendor (TicketTailor
/// <c>POST /v1/check_ins</c>). Enqueued fire-and-forget by the gate controller
/// after an admit is recorded, so the gate screen never waits on the vendor.
/// The Gate section's <c>gate_scan_events</c> row is the dedupe authority; this
/// job only keeps the vendor dashboard / vendor check-in app consistent.
/// <para>
/// <b>No automatic retries</b> (<c>Attempts = 0</c>): TicketTailor check-ins are NOT idempotent —
/// each POST creates a new check_in record (verified live 2026-06-30) — so retrying after a
/// silently-succeeded call would double-record. <c>Attempts = 0</c> disables the auto-retry filter
/// but does NOT make this exactly-once: Hangfire's at-least-once delivery can still re-run an
/// orphaned job after a worker crash, which would create a duplicate. That's accepted — at this
/// scale it's rare and a duplicate vendor-dashboard row is cosmetic; <c>gate_scan_events</c> is the
/// authority. A transient failure simply loses one mirror.
/// </para>
/// Gated behind <c>Gate:VendorMirrorEnabled</c> (default off). NB the vendor API key must have
/// Event-manager (or Admin) scope — an Order-manager key 403s on <c>/v1/check_ins</c>.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class GateVendorCheckInJob(
    ITicketVendorService vendor,
    IClock clock,
    IConfiguration configuration,
    ILogger<GateVendorCheckInJob> logger)
{
    // Live mirror: the check-in time is "now" (the scan just happened). Hangfire-invoked —
    // this signature is frozen per memory/code/hangfire-method-signature-stable.md; the
    // backfill variant below is a SEPARATE method for the same reason.
    public Task ExecuteAsync(string vendorTicketId, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(vendorTicketId, clock.GetCurrentInstant(), cancellationToken);

    // Backfill mirror: preserves the original gate admit time (unix seconds — a primitive,
    // so it serializes stably through Hangfire) instead of stamping the admin's click time.
    // Also Hangfire-invoked: signature frozen once shipped.
    public Task ExecuteBackfillAsync(string vendorTicketId, long admittedAtUnixSeconds, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(vendorTicketId, Instant.FromUnixTimeSeconds(admittedAtUnixSeconds), cancellationToken);

    private async Task ExecuteCoreAsync(string vendorTicketId, Instant occurredAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vendorTicketId))
            return;

        // Off by default until the check-in payload is verified (see TicketTailorService).
        if (!configuration.GetValue<bool>("Gate:VendorMirrorEnabled"))
        {
            logger.LogDebug(
                "Vendor check-in mirror disabled (Gate:VendorMirrorEnabled) — skipping {VendorTicketId}", vendorTicketId);
            return;
        }

        try
        {
            await vendor.CreateCheckInAsync(vendorTicketId, occurredAt, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status is >= 400 and < 500)
        {
            // A 4xx (bad payload / unknown ticket) will never succeed on retry — fail fast
            // rather than dead-lettering after all attempts. gate_scan_events is the authority.
            logger.LogWarning(
                ex, "Vendor check-in for {VendorTicketId} rejected ({Status}) — not retrying",
                vendorTicketId, (int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            // Best-effort: a transient failure (vendor 5xx / network) just loses this one mirror.
            // We do NOT retry — repeat check-ins aren't idempotent, so a retry after a silent
            // success would double-record. gate_scan_events is the authority.
            logger.LogWarning(
                ex, "Vendor check-in mirror failed for issued ticket {VendorTicketId} — not retried", vendorTicketId);
        }
    }
}
