using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Base.Configuration;
using Humans.Base.Extensions;

namespace Humans.Guide.Health;

/// <summary>
/// Checks <see cref="GitHubSettings"/>, the legal-documents repo, not the guide's source (<c>GuideSettings</c>).
/// </summary>
internal sealed class GitHubHealthCheck(IOptions<GitHubSettings> settings, ILogger<GitHubHealthCheck> logger) : IHealthCheck
{
    private readonly GitHubSettings _settings = settings.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_settings.Owner) || string.IsNullOrEmpty(_settings.Repository))
        {
            return HealthCheckResult.Degraded("GitHub repository not configured");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new GitHubClient(new ProductHeaderValue("Humans-HealthCheck"));

            if (!string.IsNullOrEmpty(_settings.AccessToken))
            {
                client.Credentials = new Credentials(_settings.AccessToken);
            }

            await client.Repository.Get(_settings.Owner, _settings.Repository).WaitAsync(cancellationToken);

            var rateLimit = client.GetLastApiInfo()?.RateLimit;
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["repository"] = $"{_settings.Owner}/{_settings.Repository}",
                ["authenticated"] = !string.IsNullOrEmpty(_settings.AccessToken)
            };

            if (rateLimit is not null)
            {
                data["rateLimit"] = rateLimit.Limit;
                data["rateLimitRemaining"] = rateLimit.Remaining;
            }

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
            logger.LogWarning(ex, "GitHub repository not found: {Owner}/{Repo}",
                _settings.Owner, _settings.Repository);
            return HealthCheckResult.Unhealthy(
                $"Repository not found: {_settings.Owner}/{_settings.Repository}",
                ex);
        }
        catch (AuthorizationException ex)
        {
            logger.LogWarning(ex, "GitHub authentication failed");
            return HealthCheckResult.Unhealthy(
                "GitHub authentication failed - check access token",
                ex);
        }
        catch (RateLimitExceededException ex)
        {
            logger.LogWarning(ex, "GitHub rate limit exceeded");
            return HealthCheckResult.Unhealthy(
                $"GitHub rate limit exceeded. Resets at {ex.Reset.ToTimeWithSeconds()}",
                ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub health check failed");
            return HealthCheckResult.Unhealthy(
                $"GitHub connection failed: {ex.Message}",
                ex);
        }
    }
}
