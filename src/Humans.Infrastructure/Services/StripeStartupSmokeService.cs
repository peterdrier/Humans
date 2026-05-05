using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Boot-time smoke probe: for each configured Stripe account key, makes a single low-risk read
/// to surface obvious misconfiguration (missing key, wrong key for environment, missing scope)
/// before the first real request hits the integration. Never blocks or fails startup; results
/// land in the structured log.
/// </summary>
/// <remarks>
/// Stripe does not expose a REST endpoint that introspects a Restricted Key's scopes
/// (verified against docs.stripe.com/keys/restricted-api-keys, 2026-05-03). The probe can confirm
/// that the scopes the integration uses ARE present — it cannot prove that no extra scopes are
/// granted. Refunds/payouts/chargebacks remain dashboard-manual regardless.
/// </remarks>
public class StripeStartupSmokeService : IHostedService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly StripeSettings _settings;
    private readonly ILogger<StripeStartupSmokeService> _logger;

    public StripeStartupSmokeService(IOptions<StripeSettings> settings, ILogger<StripeStartupSmokeService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: probe in the background so a slow Stripe doesn't delay app boot.
        _ = Task.Run(() => RunProbesAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunProbesAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);

        var probes = new List<Task>();
        if (_settings.IsConfigured)
            probes.Add(ProbeTicketsKeyAsync(cts.Token));
        else
            _logger.LogWarning("Stripe Tickets key not set — fee enrichment disabled.");

        if (_settings.IsStoreCheckoutConfigured)
            probes.Add(ProbeStoreKeyAsync(cts.Token));
        else
            _logger.LogWarning("Stripe Store key not set — Store Checkout disabled.");

        if (!_settings.IsStoreWebhookConfigured)
            _logger.LogWarning("Stripe Store webhook secret not set — Store webhook will reject all requests.");

        // Each probe catches comprehensively (StripeException, OperationCanceledException,
        // Exception) and logs its own outcome, so WhenAll cannot observe an unhandled fault.
        await Task.WhenAll(probes);
    }

    private async Task ProbeTicketsKeyAsync(CancellationToken ct)
    {
        try
        {
            var client = new StripeClient(_settings.TicketsKey);
            var service = new PaymentIntentService(client);
            await service.ListAsync(new PaymentIntentListOptions { Limit = 1 }, cancellationToken: ct);
            _logger.LogInformation("Stripe Tickets key probe succeeded (PaymentIntent read).");
        }
        catch (StripeException ex) when (IsPermissionError(ex))
        {
            _logger.LogWarning(
                "Stripe Tickets key is missing PaymentIntent read scope. Fee enrichment will fail at runtime. Stripe error: {Code} {Message}",
                ex.StripeError?.Code, ex.Message);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex,
                "Stripe Tickets key probe failed. Stripe error: {Code} {Type}",
                ex.StripeError?.Code, ex.StripeError?.Type);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stripe Tickets key probe timed out after {Timeout}.", ProbeTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe Tickets key probe failed unexpectedly.");
        }
    }

    private async Task ProbeStoreKeyAsync(CancellationToken ct)
    {
        try
        {
            var client = new StripeClient(_settings.StoreKey);
            var service = new SessionService(client);
            await service.ListAsync(new SessionListOptions { Limit = 1 }, cancellationToken: ct);
            _logger.LogInformation("Stripe Store key probe succeeded (Checkout Session list).");
        }
        catch (StripeException ex) when (IsPermissionError(ex))
        {
            _logger.LogWarning(
                "Stripe Store key is missing Checkout Session scope. Pay button will fail at runtime. Stripe error: {Code} {Message}",
                ex.StripeError?.Code, ex.Message);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex,
                "Stripe Store key probe failed. Stripe error: {Code} {Type}",
                ex.StripeError?.Code, ex.StripeError?.Type);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stripe Store key probe timed out after {Timeout}.", ProbeTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe Store key probe failed unexpectedly.");
        }
    }

    internal static bool IsPermissionError(StripeException ex) =>
        string.Equals(ex.StripeError?.Code, "permission_error", StringComparison.Ordinal) ||
        string.Equals(ex.StripeError?.Type, "permission_error", StringComparison.Ordinal);
}
