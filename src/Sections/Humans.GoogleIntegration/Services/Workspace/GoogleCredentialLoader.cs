using Google.Apis.Auth.OAuth2;
using Humans.Base.Configuration;

namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>Authenticates as the service account itself: no domain-wide delegation.</summary>
internal static class GoogleCredentialLoader
{
    /// <summary><see cref="GoogleWorkspaceSettings.ServiceAccountKeyJson"/> wins over the key path; throws when neither is set.</summary>
    public static async Task<GoogleCredential> LoadScopedAsync(
        GoogleWorkspaceSettings settings,
        CancellationToken ct,
        params string[] scopes)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(settings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory
                .FromStreamAsync<ServiceAccountCredential>(stream, ct)
                .ConfigureAwait(false))
                .ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(settings.ServiceAccountKeyPath))
        {
            await using var stream = File.OpenRead(settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory
                .FromStreamAsync<ServiceAccountCredential>(stream, ct)
                .ConfigureAwait(false))
                .ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. " +
                "Set GoogleWorkspace:ServiceAccountKeyPath or GoogleWorkspace:ServiceAccountKeyJson.");
        }

        return credential.CreateScoped(scopes);
    }
}
