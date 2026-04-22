using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="ITeamResourceGoogleClient"/>.
/// Authenticates as the service account itself (no impersonation) since the
/// resources being linked are pre-shared with that service account. Mirrors
/// the pre-migration behavior of <c>TeamResourceService</c>'s inline Google
/// API calls.
/// </summary>
public sealed class TeamResourceGoogleClient : ITeamResourceGoogleClient
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<TeamResourceGoogleClient> _logger;

    private DriveService? _driveService;
    private CloudIdentityService? _cloudIdentityService;
    private string? _serviceAccountEmail;

    public TeamResourceGoogleClient(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<TeamResourceGoogleClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DriveLookupResult> GetDriveItemAsync(
        string itemId,
        bool expectFolder,
        CancellationToken ct = default)
    {
        _ = expectFolder; // Ignored: the real connector uses the MIME type returned by Drive.
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Files.Get(itemId);
            request.Fields = "id, name, webViewLink, mimeType, parents, driveId";
            request.SupportsAllDrives = true;
            var file = await request.ExecuteAsync(ct);

            var isFolder = string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal);
            var fullPath = await BuildFolderPathAsync(drive, file, ct);

            return new DriveLookupResult(
                new DriveItem(
                    Id: file.Id,
                    Name: file.Name,
                    WebViewLink: file.WebViewLink,
                    IsFolder: isFolder,
                    FullPath: fullPath),
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google API error looking up Drive item {ItemId}: Code={Code} Message={Message}",
                itemId, ex.Error?.Code, ex.Error?.Message);
            return new DriveLookupResult(
                Item: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GroupLookupResult> LookupGroupAsync(string groupEmail, CancellationToken ct = default)
    {
        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            var lookupRequest = cloudIdentity.Groups.Lookup();
            lookupRequest.GroupKeyId = groupEmail;
            var lookupResponse = await lookupRequest.ExecuteAsync(ct);

            var fullGroup = await cloudIdentity.Groups.Get(lookupResponse.Name).ExecuteAsync(ct);
            var numericId = lookupResponse.Name["groups/".Length..];
            var emailLocal = groupEmail.Split('@')[0];
            var displayUrl = $"https://groups.google.com/a/{_settings.Domain}/g/{emailLocal}";

            return new GroupLookupResult(
                new ResolvedGroup(
                    NumericId: numericId,
                    NormalizedEmail: groupEmail,
                    DisplayName: fullGroup.DisplayName ?? groupEmail,
                    DisplayUrl: displayUrl),
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google API error looking up Group {GroupEmail}: Code={Code} Message={Message}",
                groupEmail, ex.Error?.Code, ex.Error?.Message);
            return new GroupLookupResult(
                Group: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        if (_serviceAccountEmail is not null)
        {
            return _serviceAccountEmail;
        }

        _serviceAccountEmail = await ExtractServiceAccountEmailAsync(ct);
        return _serviceAccountEmail;
    }

    private async Task<string> ExtractServiceAccountEmailAsync(CancellationToken ct)
    {
        string? json = null;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            json = _settings.ServiceAccountKeyJson;
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            json = await File.ReadAllTextAsync(_settings.ServiceAccountKeyPath, ct);
        }

        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var emailElement))
            {
                return emailElement.GetString() ?? "unknown@serviceaccount.iam.gserviceaccount.com";
            }
        }

        return "unknown@serviceaccount.iam.gserviceaccount.com";
    }

    /// <summary>
    /// Walks the parent chain up to the Shared Drive root to build a display path
    /// like "SharedDrive / Parent / Child". Falls back to the item's own name
    /// when ancestor access is denied. Mirrors the pre-migration logic.
    /// </summary>
    private async Task<string> BuildFolderPathAsync(
        DriveService drive, Google.Apis.Drive.v3.Data.File file, CancellationToken ct)
    {
        // When the file IS the shared drive root, Files.Get returns "Drive" as the name.
        // Use Drives.Get to get the actual drive name.
        if (!string.IsNullOrEmpty(file.DriveId)
            && string.Equals(file.Id, file.DriveId, StringComparison.Ordinal))
        {
            try
            {
                var driveInfo = await drive.Drives.Get(file.DriveId).ExecuteAsync(ct);
                return driveInfo.Name;
            }
            catch (Google.GoogleApiException ex)
            {
                _logger.LogDebug(ex, "Service account cannot access Shared Drive metadata for {DriveId}", file.DriveId);
                return file.Name;
            }
        }

        var segments = new List<string> { file.Name };
        var currentParents = file.Parents;
        var driveId = file.DriveId;

        // Walk up the parent chain (max 10 levels to avoid infinite loops)
        for (var i = 0; i < 10 && currentParents is { Count: > 0 }; i++)
        {
            var parentId = currentParents[0];

            // Stop if we've reached the Shared Drive root
            if (string.Equals(parentId, driveId, StringComparison.Ordinal))
            {
                // Try to get the Shared Drive name
                try
                {
                    var driveInfo = await drive.Drives.Get(driveId).ExecuteAsync(ct);
                    segments.Add(driveInfo.Name);
                }
                catch (Google.GoogleApiException ex)
                {
                    _logger.LogDebug(ex, "Service account cannot access Shared Drive metadata for {DriveId}", driveId);
                }
                break;
            }

            try
            {
                var parentRequest = drive.Files.Get(parentId);
                parentRequest.Fields = "id, name, parents, driveId";
                parentRequest.SupportsAllDrives = true;
                var parent = await parentRequest.ExecuteAsync(ct);

                segments.Add(parent.Name);
                currentParents = parent.Parents;
            }
            catch (Google.GoogleApiException ex)
            {
                _logger.LogDebug(ex, "Cannot access parent folder {ParentId} — stopping path walk", parentId);
                break;
            }
        }

        segments.Reverse();
        return string.Join(" / ", segments);
    }

    private async Task<DriveService> GetDriveServiceAsync(CancellationToken ct)
    {
        if (_driveService is not null)
        {
            return _driveService;
        }

        var credential = await GetServiceAccountCredentialAsync(ct, DriveService.Scope.DriveReadonly);

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _driveService;
    }

    private async Task<CloudIdentityService> GetCloudIdentityServiceAsync(CancellationToken ct)
    {
        if (_cloudIdentityService is not null)
        {
            return _cloudIdentityService;
        }

        var credential = await GetServiceAccountCredentialAsync(ct,
            CloudIdentityService.Scope.CloudIdentityGroupsReadonly);

        _cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _cloudIdentityService;
    }

    /// <summary>
    /// Loads the service account credential WITHOUT impersonation.
    /// This authenticates as the service account itself to access pre-shared resources.
    /// </summary>
    private async Task<GoogleCredential> GetServiceAccountCredentialAsync(CancellationToken ct, params string[] scopes)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, ct)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            await using var stream = File.OpenRead(_settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, ct)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. Set ServiceAccountKeyPath or ServiceAccountKeyJson.");
        }

        // NO .CreateWithUser() — authenticate as the service account itself
        return credential.CreateScoped(scopes);
    }
}
