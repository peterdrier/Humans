using Humans.Application.Configuration;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Humans.Web.Health;

/// <summary>
/// Health check that probes the ticket vendor connector (TicketTailor in Production).
/// Mirrors <see cref="Humans.Infrastructure.Services.StripeStartupSmokeService"/>: authenticate +
/// a cheap read so a broken/missing vendor connector surfaces in <c>/health</c>
/// instead of silently no-opping at gate time.
/// Returns <see cref="HealthCheckResult.Degraded"/> when unconfigured (missing API key or EventId)
/// and <see cref="HealthCheckResult.Unhealthy"/> when the API call fails.
/// </summary>
public sealed class TicketVendorHealthCheck(
    ITicketVendorService vendorService,
    IOptions<TicketVendorSettings> settings,
    ILogger<TicketVendorHealthCheck> logger) : IHealthCheck
{
    private readonly TicketVendorSettings _settings = settings.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            return HealthCheckResult.Degraded(
                "Ticket vendor not configured — missing EventId or API key. Ticket sync will be skipped.");
        }

        try
        {
            var summary = await vendorService.GetEventSummaryAsync(_settings.EventId, cancellationToken);

            return HealthCheckResult.Healthy(
                $"Ticket vendor reachable — event '{summary.EventName}' ({summary.TicketsSold}/{summary.TotalCapacity} sold)");
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
        {
            logger.LogWarning(
                "Ticket vendor health check: authentication failed (HTTP {StatusCode}). Check TICKET_VENDOR_API_KEY.",
                (int?)ex.StatusCode);
            return HealthCheckResult.Unhealthy(
                "Ticket vendor authentication failed — check TICKET_VENDOR_API_KEY.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                "Ticket vendor health check: API unreachable (HTTP {StatusCode}).", (int?)ex.StatusCode);
            return HealthCheckResult.Unhealthy(
                $"Ticket vendor API unreachable: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Ticket vendor health check timed out.");
            return HealthCheckResult.Degraded("Ticket vendor health check timed out.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ticket vendor health check failed unexpectedly.");
            return HealthCheckResult.Unhealthy(
                $"Ticket vendor health check failed: {ex.Message}", ex);
        }
    }
}
