using System.Text.RegularExpressions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
// `Stripe` would bind to this section inside its namespaces; see StripeService.cs.
using Stripe;

namespace Humans.Stripe.Services;

/// <summary>Production uses a dashboard-configured webhook. Failures log warnings and never block boot.</summary>
internal sealed class StoreWebhookRegistrationService(
    IOptions<StripeSettings> settings,
    IOptions<GitHubSettings> githubSettings,
    IConfiguration configuration,
    ILogger<StoreWebhookRegistrationService> logger) : IHostedService
{
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(15);
    private const string EventCheckoutSessionCompleted = "checkout.session.completed";
    private const string EventCheckoutSessionAsyncPaymentSucceeded = "checkout.session.async_payment_succeeded";
    private const string EventCheckoutSessionAsyncPaymentFailed = "checkout.session.async_payment_failed";
    private const string EventCheckoutSessionExpired = "checkout.session.expired";

    // Must match the event list on the dashboard-configured QA/prod webhook.
    private static readonly IReadOnlyList<string> SubscribedEvents =
    [
        EventCheckoutSessionCompleted,
        EventCheckoutSessionAsyncPaymentSucceeded,
        EventCheckoutSessionAsyncPaymentFailed,
        EventCheckoutSessionExpired,
    ];
    private const string WebhookPath = "/Store/StripeWebhook";
    private const string OwnedHostSuffix = ".n.burn.camp";

    private static readonly Regex PrIdFromHostPattern = new(
        @"^(?<pr>\d+)" + Regex.Escape(OwnedHostSuffix) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RegisterAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RegisterAsync(CancellationToken ct)
    {
        var stripe = settings.Value;
        if (!stripe.IsWebhookRegistrarConfigured)
        {
            return;
        }

        var baseUrl = ResolveBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning(
                "Store webhook auto-registration skipped: no Email:BaseUrl configured to derive the webhook URL.");
            return;
        }

        var webhookUrl = $"{baseUrl.TrimEnd('/')}{WebhookPath}";

        // Only hosts we own: Stripe can't reach any other, and each endpoint counts against the account quota.
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
            !webhookUri.Host.EndsWith(OwnedHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Store webhook auto-registration skipped: {Url} is not a recognized PR-preview host (must end in {Suffix}).",
                webhookUrl, OwnedHostSuffix);
            return;
        }

        // Warning, not Information, so production filters don't hide the only confirmation that the
        // in-memory signing secret was stamped.
        logger.LogWarning("Auto-registering Stripe webhook for PR-preview env at {Url}…", webhookUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RegistrationTimeout);

        try
        {
            var client = new StripeClient(stripe.WebhookRegistrarKey);
            var service = new WebhookEndpointService(client);

            await DeleteObsoleteEndpointsAsync(service, webhookUrl, cts.Token);

            var created = await service.CreateAsync(new WebhookEndpointCreateOptions
            {
                Url = webhookUrl,
                EnabledEvents = [.. SubscribedEvents],
                Description = "Humans Store — auto-registered (ephemeral env)",
            }, cancellationToken: cts.Token);

            if (string.IsNullOrEmpty(created.Secret))
            {
                logger.LogWarning(
                    "Stripe returned a webhook endpoint with no signing secret for {Url}; webhook will reject deliveries.",
                    webhookUrl);
                return;
            }

            stripe.StoreWebhookSecret = created.Secret;
            logger.LogWarning(
                "Auto-registered Stripe webhook {EndpointId} at {Url} (events: {Events}); STRIPE_STORE_WEBHOOK_SECRET stamped in-memory.",
                created.Id, webhookUrl, string.Join(", ", SubscribedEvents));
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe Store key is missing webhook_endpoint:read/write scope — webhook auto-registration not possible. {Message}",
                ex.Message);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex,
                "Stripe webhook auto-registration failed. Code: {Code}",
                ex.StripeError?.Code);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Stripe webhook auto-registration timed out after {Timeout}.", RegistrationTimeout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stripe webhook auto-registration failed unexpectedly.");
        }
    }

    /// <summary>
    /// Deletes endpoints of closed PRs, then our own URL (about to be recreated with a fresh secret). The
    /// cross-PR sweep needs the open-PR list; the own-URL delete runs without it.
    /// </summary>
    private async Task DeleteObsoleteEndpointsAsync(
        WebhookEndpointService service, string ownWebhookUrl, CancellationToken ct)
    {
        var openPrs = await TryListOpenPullRequestsAsync(ct);

        var ours = new List<WebhookEndpoint>();

        var listed = await service.ListAsync(new WebhookEndpointListOptions { Limit = 100 }, cancellationToken: ct);
        foreach (var endpoint in listed.Data)
        {
            // Only our own endpoints, even though the registrar key is account-scoped.
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.EndsWith(OwnedHostSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!uri.AbsolutePath.Equals(WebhookPath, StringComparison.Ordinal)) continue;

            if (string.Equals(endpoint.Url, ownWebhookUrl, StringComparison.OrdinalIgnoreCase))
            {
                // Deleted last, below.
                ours.Add(endpoint);
                continue;
            }

            if (openPrs is null) continue;

            var match = PrIdFromHostPattern.Match(uri.Host);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["pr"].Value, System.Globalization.CultureInfo.InvariantCulture, out var prId))
                continue;

            if (openPrs.Contains(prId)) continue;

            try
            {
                await service.DeleteAsync(endpoint.Id, cancellationToken: ct);
                logger.LogInformation(
                    "Deleted Stripe webhook {EndpointId} for closed PR #{PrId} ({Url}).",
                    endpoint.Id, prId, endpoint.Url);
            }
            catch (StripeException ex)
            {
                // Another PR's registrar may have raced ahead; a 404 is fine.
                logger.LogDebug(ex,
                    "Could not delete webhook {EndpointId} during sweep — likely already gone.",
                    endpoint.Id);
            }
        }

        // Our own URL goes last: a failure here aborts registration rather than leave a duplicate endpoint
        // under a secret we no longer hold, and doing it last means any abort leaves the account as this
        // boot found it.
        foreach (var endpoint in ours)
        {
            await service.DeleteAsync(endpoint.Id, cancellationToken: ct);
            logger.LogInformation(
                "Deleted Stripe webhook {EndpointId} pointing at {Url} (current-PR cleanup).",
                endpoint.Id, endpoint.Url);
        }
    }

    /// <summary>Null means unknown, never "none": an empty set would delete every other preview's endpoint.</summary>
    private async Task<ISet<int>?> TryListOpenPullRequestsAsync(CancellationToken ct)
    {
        var stripe = settings.Value;
        var github = githubSettings.Value;

        if (!stripe.IsWebhookCleanupConfigured || string.IsNullOrEmpty(github.AccessToken))
        {
            logger.LogInformation(
                "Cross-PR webhook sweep skipped: missing Stripe:WebhookCleanupOwner/Repository or GitHub:AccessToken.");
            return null;
        }

        try
        {
            var client = new GitHubClient(new ProductHeaderValue("NobodiesHumans"))
            {
                Credentials = new Credentials(github.AccessToken),
            };
            return await ListOpenPullRequestsAsync(
                stripe.WebhookCleanupGitHubOwner,
                stripe.WebhookCleanupGitHubRepository,
                client.PullRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Cross-PR webhook sweep skipped: failed to list open PRs from {Owner}/{Repo}.",
                stripe.WebhookCleanupGitHubOwner, stripe.WebhookCleanupGitHubRepository);
            return null;
        }
    }

    internal static async Task<ISet<int>> ListOpenPullRequestsAsync(
        string owner, string repo, IPullRequestsClient client, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Octokit's paged read has no token overload; bound the wait by the registrar deadline.
        var prs = await client.GetAllForRepository(owner, repo, new PullRequestRequest
        {
            State = ItemStateFilter.Open,
        }).WaitAsync(ct);
        return prs.Select(p => p.Number).ToHashSet();
    }

    /// <summary>Email:BaseUrl is already set per environment.</summary>
    private string? ResolveBaseUrl() => configuration["Email:BaseUrl"];
}
