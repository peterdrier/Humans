using System.Text.RegularExpressions;
using Humans.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
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
/// Same boot also runs a cross-PR sweep (<see cref="SweepStaleEndpointsAsync"/>):
/// lists every <c>n.burn.camp</c> endpoint on the account, parses the leading
/// PR-id off the host, queries GitHub for currently-open PRs on the configured
/// fork, and deletes endpoints whose PR is no longer open. Self-gated on
/// <see cref="StripeSettings.IsWebhookCleanupConfigured"/> + a GitHub access
/// token; no separate GH Action is involved. See <c>docs/sections/Store.md</c>
/// "Stripe Configuration" for the env-var contract.
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
    private const string EventCheckoutSessionAsyncPaymentSucceeded = "checkout.session.async_payment_succeeded";
    private const string EventCheckoutSessionAsyncPaymentFailed = "checkout.session.async_payment_failed";
    private const string EventCheckoutSessionExpired = "checkout.session.expired";

    // PR-preview subscribes to the same 4 events QA/prod register manually so async-payment
    // behavior is observable end-to-end. Today the controller only acts on `completed`; the
    // other three log at Warning until the async-payment state machine ships
    // (nobodies-collective/Humans#638).
    private static readonly IReadOnlyList<string> SubscribedEvents =
    [
        EventCheckoutSessionCompleted,
        EventCheckoutSessionAsyncPaymentSucceeded,
        EventCheckoutSessionAsyncPaymentFailed,
        EventCheckoutSessionExpired,
    ];
    private const string WebhookPath = "/Store/StripeWebhook";
    private const string OwnedHostSuffix = ".n.burn.camp";

    /// <summary>Matches the leading PR-id label in a host like "37.n.burn.camp".</summary>
    private static readonly Regex PrIdFromHostPattern = new(
        @"^(?<pr>\d+)" + Regex.Escape(OwnedHostSuffix) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly IOptions<StripeSettings> _settings;
    private readonly IOptions<GitHubSettings> _githubSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StoreWebhookRegistrationService> _logger;

    public StoreWebhookRegistrationService(
        IOptions<StripeSettings> settings,
        IOptions<GitHubSettings> githubSettings,
        IConfiguration configuration,
        ILogger<StoreWebhookRegistrationService> logger)
    {
        _settings = settings;
        _githubSettings = githubSettings;
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

        // Only auto-register on hosts we own. Dev (localhost / nuc.home) and any other
        // unrecognized host should never register — Stripe couldn't reach it anyway, and
        // the endpoint would just burn against the per-account quota.
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
            !webhookUri.Host.EndsWith(OwnedHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Store webhook auto-registration skipped: {Url} is not a recognized PR-preview host (must end in {Suffix}).",
                webhookUrl, OwnedHostSuffix);
            return;
        }

        // Warning level — this is a rare, boot-time-only event in PR-preview environments
        // and the success line is the only on-host confirmation that the in-memory
        // STRIPE_STORE_WEBHOOK_SECRET was stamped. Information would be filtered out in prod.
        _logger.LogWarning("Auto-registering Stripe webhook for PR-preview env at {Url}…", webhookUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RegistrationTimeout);

        try
        {
            var client = new StripeClient(settings.WebhookRegistrarKey);
            var service = new WebhookEndpointService(client);

            await SweepStaleEndpointsAsync(service, webhookUrl, cts.Token);
            await DeleteExistingForUrlAsync(service, webhookUrl, cts.Token);

            var created = await service.CreateAsync(new WebhookEndpointCreateOptions
            {
                Url = webhookUrl,
                EnabledEvents = [.. SubscribedEvents],
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
            _logger.LogWarning(
                "Auto-registered Stripe webhook {EndpointId} at {Url} (events: {Events}); STRIPE_STORE_WEBHOOK_SECRET stamped in-memory.",
                created.Id, webhookUrl, string.Join(", ", SubscribedEvents));
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
                "Deleted Stripe webhook {EndpointId} pointing at {Url} (current-PR cleanup).",
                stale.Id, webhookUrl);
        }
    }

    /// <summary>
    /// Cross-PR sweep: deletes endpoints whose URL host matches <c>{N}.n.burn.camp</c>
    /// where PR <c>{N}</c> is no longer open on the configured fork. Skipped when
    /// <see cref="StripeSettings.WebhookCleanupGitHubOwner"/>/<c>WebhookCleanupGitHubRepository</c>
    /// or <see cref="GitHubSettings.AccessToken"/> are unset.
    /// </summary>
    private async Task SweepStaleEndpointsAsync(
        WebhookEndpointService service, string ownWebhookUrl, CancellationToken ct)
    {
        var settings = _settings.Value;
        var github = _githubSettings.Value;

        if (!settings.IsWebhookCleanupConfigured || string.IsNullOrEmpty(github.AccessToken))
        {
            _logger.LogInformation(
                "Cross-PR webhook sweep skipped: missing Stripe:WebhookCleanupOwner/Repository or GitHub:AccessToken.");
            return;
        }

        ISet<int> openPrs;
        try
        {
            openPrs = await ListOpenPullRequestsAsync(
                settings.WebhookCleanupGitHubOwner,
                settings.WebhookCleanupGitHubRepository,
                github.AccessToken,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cross-PR webhook sweep skipped: failed to list open PRs from {Owner}/{Repo}.",
                settings.WebhookCleanupGitHubOwner, settings.WebhookCleanupGitHubRepository);
            return;
        }

        var listed = await service.ListAsync(new WebhookEndpointListOptions { Limit = 100 }, cancellationToken: ct);
        foreach (var endpoint in listed.Data)
        {
            // Only touch endpoints we own (the n.burn.camp suffix). Defense in depth — the
            // registrar key is account-scoped, but the sweep should never delete an unrelated
            // integration's webhook even if one ever shared this Stripe account.
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.EndsWith(OwnedHostSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!uri.AbsolutePath.Equals(WebhookPath, StringComparison.Ordinal)) continue;

            // Don't sweep our own endpoint here — the per-URL DeleteExistingForUrlAsync handles that.
            if (string.Equals(endpoint.Url, ownWebhookUrl, StringComparison.OrdinalIgnoreCase)) continue;

            var match = PrIdFromHostPattern.Match(uri.Host);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["pr"].Value, System.Globalization.CultureInfo.InvariantCulture, out var prId))
                continue;

            if (openPrs.Contains(prId)) continue;

            try
            {
                await service.DeleteAsync(endpoint.Id, cancellationToken: ct);
                _logger.LogInformation(
                    "Deleted Stripe webhook {EndpointId} for closed PR #{PrId} ({Url}).",
                    endpoint.Id, prId, endpoint.Url);
            }
            catch (StripeException ex)
            {
                // Idempotent — another PR's registrar may have raced ahead. 404s are fine.
                _logger.LogDebug(ex,
                    "Could not delete webhook {EndpointId} during sweep — likely already gone.",
                    endpoint.Id);
            }
        }
    }

    private static async Task<ISet<int>> ListOpenPullRequestsAsync(
        string owner, string repo, string accessToken, CancellationToken ct)
    {
        var client = new GitHubClient(new ProductHeaderValue("NobodiesHumans"))
        {
            Credentials = new Credentials(accessToken),
        };
        var prs = await client.PullRequest.GetAllForRepository(owner, repo, new PullRequestRequest
        {
            State = ItemStateFilter.Open,
        });
        return prs.Select(p => p.Number).ToHashSet();
    }

    /// <summary>
    /// Public hostname this app is reachable at. Reuses Email:BaseUrl, which Coolify
    /// already sets per environment for transactional email links.
    /// </summary>
    private string? ResolveBaseUrl() => _configuration["Email:BaseUrl"];
}
