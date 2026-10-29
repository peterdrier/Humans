using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// `Stripe` would bind to this section inside its namespaces; see StripeService.cs.
using Stripe;
using Stripe.Checkout;

namespace Humans.Stripe.Services;

/// <summary>
/// One low-risk read per configured key at boot; never blocks or fails startup. Stripe cannot introspect a
/// restricted key's scopes, so this proves the scopes used are present, not that no extra ones are.
/// </summary>
internal sealed class StripeStartupSmokeService(IOptions<StripeSettings> settings, ILogger<StripeStartupSmokeService> logger)
    : IHostedService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly StripeSettings _settings = settings.Value;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget so a slow Stripe doesn't delay boot.
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
            probes.Add(ProbeAsync("Tickets", "PaymentIntent read", "Fee enrichment",
                token => new PaymentIntentService(new StripeClient(_settings.TicketsKey))
                    .ListAsync(new PaymentIntentListOptions { Limit = 1 }, cancellationToken: token),
                cts.Token));
        else
            logger.LogWarning("Stripe Tickets key not set — fee enrichment disabled.");

        if (_settings.IsStoreCheckoutConfigured)
            probes.Add(ProbeAsync("Store", "Checkout Session", "Pay button",
                token => new SessionService(new StripeClient(_settings.StoreKey))
                    .ListAsync(new SessionListOptions { Limit = 1 }, cancellationToken: token),
                cts.Token));
        else
            logger.LogWarning("Stripe Store key not set — Store Checkout disabled.");

        if (!_settings.IsStoreWebhookConfigured)
            logger.LogWarning("Stripe Store webhook secret not set — Store webhook will reject all requests.");

        // Each probe catches and logs everything itself, so WhenAll never sees a fault.
        await Task.WhenAll(probes);
    }

    private async Task ProbeAsync(
        string key, string scope, string consequence, Func<CancellationToken, Task> read, CancellationToken ct)
    {
        try
        {
            await read(ct);
            logger.LogInformation("Stripe {Key} key probe succeeded ({Scope}).", key, scope);
        }
        catch (StripeException ex) when (IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe {Key} key is missing {Scope} scope. {Consequence} will fail at runtime. Stripe error: {Code} {Message}",
                key, scope, consequence, ex.StripeError?.Code, ex.Message);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex,
                "Stripe {Key} key probe failed. Stripe error: {Code} {Type}",
                key, ex.StripeError?.Code, ex.StripeError?.Type);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stripe {Key} key probe timed out after {Timeout}.", key, ProbeTimeout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stripe {Key} key probe failed unexpectedly.", key);
        }
    }

    internal static bool IsPermissionError(StripeException ex) =>
        string.Equals(ex.StripeError?.Code, "permission_error", StringComparison.Ordinal) ||
        string.Equals(ex.StripeError?.Type, "permission_error", StringComparison.Ordinal);
}
