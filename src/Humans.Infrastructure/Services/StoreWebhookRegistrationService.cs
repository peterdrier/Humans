using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Auto-registers the Store Stripe webhook endpoint with Stripe at boot for
/// short-lived environments (PR previews, ephemeral QA) where setting up a
/// webhook in the Stripe dashboard per env is impractical.
/// </summary>
/// <remarks>
/// <para>
/// Self-gated: runs IFF <c>STRIPE_STORE_WEBHOOK_REGISTRAR_KEY</c> is set. That
/// dedicated key carries <c>webhook_endpoint:read/write</c> scope and lives
/// only in ephemeral envs. Production deliberately does not set it and uses a
/// dashboard-configured webhook with a stable signing secret instead. Keeping
/// the registrar key separate from <see cref="StripeSettings.StoreKey"/>
/// preserves PR-preview testing fidelity: the Pay-button checkout path runs
/// against a key with the production-narrow scope.
/// </para>
/// <para>
/// At boot: lists existing webhooks pointing at this env's URL, deletes them,
/// creates a fresh one, and stamps the returned signing secret onto
/// <see cref="StripeSettings.StoreWebhookSecret"/> so the webhook controller can
/// verify subsequent deliveries. The signing secret is in-memory only — a
/// process restart re-registers and gets a new one. Stripe only returns the
/// secret at creation time; there is no fetch path.
/// </para>
/// <para>
/// Cross-PR cleanup of stale endpoints (PR closes, env disappears) is the job
/// of the existing PR-close GitHub Action, not this service. See
/// <c>docs/sections/Store.md</c> "Stripe Configuration".
/// </para>
/// <para>
/// Failures are logged as warnings and do not block boot. If registration
/// fails, the controller returns 503 on subsequent webhook deliveries —
/// behavior identical to "no webhook secret configured."
/// </para>
/// </remarks>
public class StoreWebhookRegistrationService : IHostedService
{
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(15);
    private const string EventCheckoutSessionCompleted = "checkout.session.completed";
    private const string WebhookPath = "/Store/StripeWebhook";

    private readonly IOptions<StripeSettings> _settings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StoreWebhookRegistrationService> _logger;

    public StoreWebhookRegistrationService(
        IOptions<StripeSettings> settings,
        IConfiguration configuration,
        ILogger<StoreWebhookRegistrationService> logger)
    {
        _settings = settings;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Background fire-and-forget — never block boot on a Stripe API call.
        _ = Task.Run(() => RegisterAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RegisterAsync(CancellationToken ct)
    {
        var settings = _settings.Value;
        if (!settings.IsWebhookRegistrarConfigured)
        {
            // Quiet — production and QA deliberately don't set the registrar key.
            return;
        }

        var baseUrl = ResolveBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning(
                "Store webhook auto-registration skipped: no Email:BaseUrl configured to derive the webhook URL.");
            return;
        }

        var webhookUrl = $"{baseUrl.TrimEnd('/')}{WebhookPath}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RegistrationTimeout);

        try
        {
            var client = new StripeClient(settings.WebhookRegistrarKey);
            var service = new WebhookEndpointService(client);

            await DeleteExistingForUrlAsync(service, webhookUrl, cts.Token);

            var created = await service.CreateAsync(new WebhookEndpointCreateOptions
            {
                Url = webhookUrl,
                EnabledEvents = [EventCheckoutSessionCompleted],
                Description = "Humans Store — auto-registered (ephemeral env)",
            }, cancellationToken: cts.Token);

            if (string.IsNullOrEmpty(created.Secret))
            {
                _logger.LogWarning(
                    "Stripe returned a webhook endpoint with no signing secret for {Url}; webhook will reject deliveries.",
                    webhookUrl);
                return;
            }

            settings.StoreWebhookSecret = created.Secret;
            _logger.LogInformation(
                "Auto-registered Stripe webhook {EndpointId} at {Url} (events: {Events}).",
                created.Id, webhookUrl, EventCheckoutSessionCompleted);
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            _logger.LogWarning(
                "Stripe Store key is missing webhook_endpoint:read/write scope — webhook auto-registration not possible. {Message}",
                ex.Message);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex,
                "Stripe webhook auto-registration failed. Code: {Code}",
                ex.StripeError?.Code);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Stripe webhook auto-registration timed out after {Timeout}.", RegistrationTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe webhook auto-registration failed unexpectedly.");
        }
    }

    private async Task DeleteExistingForUrlAsync(
        WebhookEndpointService service, string webhookUrl, CancellationToken ct)
    {
        var listed = await service.ListAsync(new WebhookEndpointListOptions { Limit = 100 }, cancellationToken: ct);
        var matches = listed.Data
            .Where(w => string.Equals(w.Url, webhookUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var stale in matches)
        {
            await service.DeleteAsync(stale.Id, cancellationToken: ct);
            _logger.LogInformation(
                "Deleted stale Stripe webhook {EndpointId} pointing at {Url}.",
                stale.Id, webhookUrl);
        }
    }

    /// <summary>
    /// Public hostname this app is reachable at. Reuses Email:BaseUrl, which Coolify
    /// already sets per environment for transactional email links.
    /// </summary>
    private string? ResolveBaseUrl() => _configuration["Email:BaseUrl"];
}
