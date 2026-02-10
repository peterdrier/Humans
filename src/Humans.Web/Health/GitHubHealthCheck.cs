using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Infrastructure.Configuration;

namespace Humans.Web.Health;

/// <summary>
/// Health check that validates GitHub API connectivity and repository access.
/// Verifies authentication and that the configured repository is accessible.
/// </summary>
public class GitHubHealthCheck : IHealthCheck
{
    private readonly GitHubSettings _settings;
    private readonly ILogger<GitHubHealthCheck> _logger;

    public GitHubHealthCheck(
        IOptions<GitHubSettings> settings,
        ILogger<GitHubHealthCheck> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Skip detailed check if no repository is configured
        if (string.IsNullOrEmpty(_settings.Owner) || string.IsNullOrEmpty(_settings.Repository))
        {
            return HealthCheckResult.Degraded("GitHub repository not configured");
        }

        try
        {
            var client = new GitHubClient(new ProductHeaderValue("Humans-HealthCheck"));

            if (!string.IsNullOrEmpty(_settings.AccessToken))
            {
                client.Credentials = new Credentials(_settings.AccessToken);
            }

            // Verify we can access the repository
            var repo = await client.Repository.Get(_settings.Owner, _settings.Repository);

            // Check rate limit status
            var rateLimit = client.GetLastApiInfo()?.RateLimit;
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["repository"] = $"{_settings.Owner}/{_settings.Repository}",
                ["authenticated"] = !string.IsNullOrEmpty(_settings.AccessToken)
            };

            if (rateLimit != null)
            {
                data["rateLimit"] = rateLimit.Limit;
                data["rateLimitRemaining"] = rateLimit.Remaining;
            }

            // Warn if rate limit is low
            if (rateLimit?.Remaining < 10)
            {
                return HealthCheckResult.Degraded(
                    $"GitHub API rate limit low: {rateLimit.Remaining}/{rateLimit.Limit} remaining",
                    data: data);
            }

            return HealthCheckResult.Healthy(
                $"GitHub repository {_settings.Owner}/{_settings.Repository} accessible",
                data);
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "GitHub repository not found: {Owner}/{Repo}",
                _settings.Owner, _settings.Repository);
            return HealthCheckResult.Unhealthy(
                $"Repository not found: {_settings.Owner}/{_settings.Repository}",
                ex);
        }
        catch (AuthorizationException ex)
        {
            _logger.LogWarning(ex, "GitHub authentication failed");
            return HealthCheckResult.Unhealthy(
                "GitHub authentication failed - check access token",
                ex);
        }
        catch (RateLimitExceededException ex)
        {
            _logger.LogWarning(ex, "GitHub rate limit exceeded");
            return HealthCheckResult.Unhealthy(
                $"GitHub rate limit exceeded. Resets at {ex.Reset:HH:mm:ss}",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub health check failed");
            return HealthCheckResult.Unhealthy(
                $"GitHub connection failed: {ex.Message}",
                ex);
        }
    }
}
