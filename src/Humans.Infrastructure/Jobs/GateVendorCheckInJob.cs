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
/// Hangfire retries on failure (the vendor treats repeat check-ins idempotently).
/// Gated behind <c>Gate:VendorMirrorEnabled</c> (default off) until the
/// TicketTailor check-in payload is verified against a live API key — a wrong
/// body 4xx-fails silently and permanently, so the mirror stays off by default.
/// </summary>
[AutomaticRetry(Attempts = 5)]
public sealed class GateVendorCheckInJob(
    ITicketVendorService vendor,
    IClock clock,
    IConfiguration configuration,
    ILogger<GateVendorCheckInJob> logger)
{
    public async Task ExecuteAsync(string vendorTicketId, CancellationToken cancellationToken = default)
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
            await vendor.CreateCheckInAsync(vendorTicketId, clock.GetCurrentInstant(), cancellationToken);
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
            logger.LogWarning(
                ex, "Vendor check-in mirror failed for issued ticket {VendorTicketId}; Hangfire will retry", vendorTicketId);
            throw;
        }
    }
}
