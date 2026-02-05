using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Profiles.Infrastructure.Configuration;

namespace Profiles.Web.Health;

/// <summary>
/// Health check that validates Google Workspace API connectivity and authentication.
/// Verifies service account credentials and domain-wide delegation are properly configured.
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

        if (string.IsNullOrEmpty(_settings.ImpersonateUser))
        {
            return HealthCheckResult.Unhealthy("ImpersonateUser not configured");
        }

        try
        {
            var credential = await GetCredentialAsync(cancellationToken);
            var directoryService = new DirectoryService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Nobodies Profiles Health Check"
            });

            // Make a simple API call to verify authentication and delegation
            // List groups with max 1 result - minimal API call that validates everything
            var request = directoryService.Groups.List();
            request.Domain = _settings.Domain;
            request.MaxResults = 1;
            await request.ExecuteAsync(cancellationToken);

            return HealthCheckResult.Healthy("Google Workspace API authentication successful");
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 403)
        {
            _logger.LogWarning(ex, "Google Workspace authorization failed - check domain-wide delegation");
            return HealthCheckResult.Unhealthy(
                "Authorization failed - verify domain-wide delegation is configured for the service account",
                ex);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 401)
        {
            _logger.LogWarning(ex, "Google Workspace authentication failed");
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
                $"Google Workspace connection failed: {ex.Message}",
                ex);
        }
    }

    private async Task<GoogleCredential> GetCredentialAsync(CancellationToken cancellationToken)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            // Use CredentialFactory for secure credential loading
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

        return credential
            .CreateScoped(DirectoryService.Scope.AdminDirectoryGroupReadonly)
            .CreateWithUser(_settings.ImpersonateUser);
    }
}
