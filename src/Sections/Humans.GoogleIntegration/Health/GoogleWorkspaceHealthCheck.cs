using Google.Apis.CloudIdentity.v1;
using Humans.GoogleIntegration.Services.Workspace;
using Google.Apis.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Humans.Base.Configuration;

namespace Humans.GoogleIntegration.Health;

/// <summary>
/// Health check that validates Google service account credentials and API access.
/// Verifies the service account can authenticate and access the Cloud Identity Groups API.
/// </summary>
internal sealed class GoogleWorkspaceHealthCheck(
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<GoogleWorkspaceHealthCheck> logger) : IHealthCheck
{
    private readonly GoogleWorkspaceSettings _settings = settings.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_settings.ServiceAccountKeyPath) &&
            string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            return HealthCheckResult.Degraded("Google Workspace not configured");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credential = await GoogleCredentialLoader.LoadScopedAsync(
                _settings, cancellationToken, CloudIdentityService.Scope.CloudIdentityGroupsReadonly);
            using var cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Humans Health Check"
            });

            // List groups with max 1 result — validates credential + Cloud Identity API access
            var request = cloudIdentityService.Groups.List();
            request.Parent = $"customers/{_settings.CustomerId}";
            request.PageSize = 1;
            await request.ExecuteAsync(cancellationToken);

            return HealthCheckResult.Healthy("Google service account authentication successful");
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 403)
        {
            logger.LogWarning(ex, "Google API authorization failed - check service account permissions");
            return HealthCheckResult.Unhealthy(
                "Authorization failed - ensure the service account has Cloud Identity Groups access",
                ex);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 401)
        {
            logger.LogWarning(ex, "Google API authentication failed");
            return HealthCheckResult.Unhealthy(
                "Authentication failed - check service account credentials",
                ex);
        }
        catch (FileNotFoundException ex)
        {
            logger.LogWarning(ex, "Service account key file not found");
            return HealthCheckResult.Unhealthy(
                $"Service account key file not found: {_settings.ServiceAccountKeyPath}",
                ex);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Google Workspace health check failed");
            return HealthCheckResult.Unhealthy(
                $"Google service account check failed: {ex.Message}",
                ex);
        }
    }

}
