using Hangfire;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using NodaTime;

namespace Humans.Gate.Jobs;

/// <summary>
/// Best-effort mirror of a gate admit to the vendor; <c>gate_scan_events</c> stays the authority. Off unless
/// <c>Gate:VendorMirrorEnabled</c>; the vendor API key needs Event-manager (or Admin) scope or it 403s.
/// </summary>
/// <remarks>
/// No retries: vendor check-ins are not idempotent, so a retry after a silent success double-records. Hangfire's
/// at-least-once delivery can still re-run an orphaned job; at this scale that cosmetic duplicate is accepted.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class GateVendorCheckInJob(
    ITicketVendorMirror vendor,
    IClock clock,
    IConfiguration configuration,
    ILogger<GateVendorCheckInJob> logger)
{
    // Hangfire-invoked: the signature is frozen once shipped, so the backfill variant is a separate method.
    public Task ExecuteAsync(string vendorTicketId, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(vendorTicketId, clock.GetCurrentInstant(), cancellationToken);

    // Keeps the original admit time (unix seconds, a primitive that serializes stably) instead of the admin's click
    // time. Hangfire-invoked: signature frozen.
    public Task ExecuteBackfillAsync(string vendorTicketId, long admittedAtUnixSeconds, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(vendorTicketId, Instant.FromUnixTimeSeconds(admittedAtUnixSeconds), cancellationToken);

    private async Task ExecuteCoreAsync(string vendorTicketId, Instant occurredAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vendorTicketId))
            return;

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
            // A 4xx will never succeed on retry.
            logger.LogWarning(
                ex, "Vendor check-in for {VendorTicketId} rejected ({Status}) — not retrying",
                vendorTicketId, (int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            // A transient failure just loses this one mirror: no retry, since repeat check-ins double-record.
            logger.LogWarning(
                ex, "Vendor check-in mirror failed for issued ticket {VendorTicketId} — not retried", vendorTicketId);
        }
    }
}
