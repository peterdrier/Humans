using Google.Apis.Auth.OAuth2;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Shared helper for loading service-account credentials from the configured
/// <see cref="GoogleWorkspaceSettings"/> inline JSON blob or on-disk key file.
/// Used by all real Google-backed bridge clients in
/// <c>Humans.Infrastructure.Services.GoogleWorkspace</c>. Authenticates as
/// the service account itself — no domain-wide delegation / impersonation —
/// matching the pre-migration behaviour of the inline credential loaders in
/// <c>GoogleWorkspaceSyncService</c> and the other connectors (which each
/// held their own copy before §15 Part 2a).
/// </summary>
internal static class GoogleCredentialLoader
{
    /// <summary>
    /// Loads a <see cref="GoogleCredential"/> from the configured service-
    /// account key (<see cref="GoogleWorkspaceSettings.ServiceAccountKeyJson"/>
    /// wins over <see cref="GoogleWorkspaceSettings.ServiceAccountKeyPath"/>)
    /// and restricts it to the requested <paramref name="scopes"/>. Throws
    /// <see cref="InvalidOperationException"/> when neither key source is
    /// configured.
    /// </summary>
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
