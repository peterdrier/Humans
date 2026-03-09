using Google.Apis.CloudIdentity.v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Humans.Infrastructure.Configuration;

namespace Humans.Web.Health;

/// <summary>
/// Health check that validates Google service account credentials and API access.
/// Verifies the service account can authenticate and access the Cloud Identity Groups API.
/// </summary>
public class GoogleWorkspaceHealthCheck : IHealthCheck
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleWorkspaceHealthCheck> _logger;

    public GoogleWorkspaceHealthCheck(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleWorkspaceHealthCheck> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Skip check if not configured
        if (string.IsNullOrEmpty(_settings.ServiceAccountKeyPath) &&
            string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            return HealthCheckResult.Degraded("Google Workspace not configured");
        }

        try
        {
            var credential = await GetCredentialAsync(cancellationToken);
            var cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
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
            _logger.LogWarning(ex, "Google API authorization failed - check service account permissions");
            return HealthCheckResult.Unhealthy(
                "Authorization failed - ensure the service account has Cloud Identity Groups access",
                ex);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 401)
        {
            _logger.LogWarning(ex, "Google API authentication failed");
            return HealthCheckResult.Unhealthy(
                "Authentication failed - check service account credentials",
                ex);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Service account key file not found");
            return HealthCheckResult.Unhealthy(
                $"Service account key file not found: {_settings.ServiceAccountKeyPath}",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Workspace health check failed");
            return HealthCheckResult.Unhealthy(
                $"Google service account check failed: {ex.Message}",
                ex);
        }
    }

    private async Task<GoogleCredential> GetCredentialAsync(CancellationToken cancellationToken)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, cancellationToken)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            await using var stream = File.OpenRead(_settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, cancellationToken)
                .ConfigureAwait(false)).ToGoogleCredential();
        }

        return credential.CreateScoped(CloudIdentityService.Scope.CloudIdentityGroupsReadonly);
    }
}
