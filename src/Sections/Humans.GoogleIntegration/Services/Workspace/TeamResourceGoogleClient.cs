using System.Text.Json;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Humans.Base.Extensions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;

namespace Humans.GoogleIntegration.Services.Workspace;

internal sealed class TeamResourceGoogleClient(
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<TeamResourceGoogleClient> logger,
    IGoogleDrivePermissionsClient drivePermissions) : ITeamResourceGoogleClient
{
    private readonly GoogleWorkspaceSettings _settings = settings.Value;

    private DriveService? _driveService;
    private CloudIdentityService? _cloudIdentityService;
    private string? _serviceAccountEmail;

    public async Task<DriveLookupResult> GetDriveItemAsync(
        string itemId,
        bool expectFolder,
        CancellationToken ct = default)
    {
        using var timer = logger.TimeOperation();
        _ = expectFolder; // Ignored: the real connector uses the MIME type returned by Drive.
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Files.Get(itemId);
            request.Fields = "id, name, webViewLink, mimeType, parents, driveId";
            request.SupportsAllDrives = true;
            var file = await request.ExecuteAsync(ct);
            var fileId = file.Id ?? itemId;

            var isFolder = string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal);
            var fullPath = await DriveFolderPath.ResolveAsync(
                drivePermissions, fileId, logger, ct,
                new DriveFileMetadata(fileId, file.Name, file.Parents as IReadOnlyList<string>, file.DriveId,
                    file.InheritedPermissionsDisabled)) ?? file.Name;

            return new DriveLookupResult(
                new DriveItem(
                    Id: fileId,
                    Name: file.Name,
                    WebViewLink: file.WebViewLink,
                    IsFolder: isFolder,
                    FullPath: fullPath),
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            logger.LogWarning(ex, "Google API error looking up Drive item {ItemId}: Code={Code} Message={Message}",
                itemId, ex.Error?.Code, ex.Error?.Message);
            return new DriveLookupResult(
                Item: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GroupLookupResult> LookupGroupAsync(string groupEmail, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
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
            logger.LogWarning(ex, "Google API error looking up Group {GroupEmail}: Code={Code} Message={Message}",
                groupEmail, ex.Error?.Code, ex.Error?.Message);
            return new GroupLookupResult(
                Group: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (_serviceAccountEmail is not null)
        {
            return _serviceAccountEmail;
        }

        var json = await GoogleCredentialLoader.GetServiceAccountJsonAsync(_settings, ct);

        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var emailElement))
            {
                _serviceAccountEmail = emailElement.GetString() ?? "unknown@serviceaccount.iam.gserviceaccount.com";
                return _serviceAccountEmail;
            }
        }

        _serviceAccountEmail = "unknown@serviceaccount.iam.gserviceaccount.com";
        return _serviceAccountEmail;
    }

    private async Task<DriveService> GetDriveServiceAsync(CancellationToken ct)
    {
        if (_driveService is not null)
        {
            return _driveService;
        }

        var credential = await GoogleCredentialLoader.LoadScopedAsync(_settings, ct, DriveService.Scope.DriveReadonly);

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

        var credential = await GoogleCredentialLoader.LoadScopedAsync(_settings, ct,
            CloudIdentityService.Scope.CloudIdentityGroupsReadonly);

        _cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _cloudIdentityService;
    }

}
