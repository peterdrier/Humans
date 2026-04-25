using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.DriveActivity.v2;
using Google.Apis.DriveActivity.v2.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Humans.Infrastructure.Configuration;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IGoogleDriveActivityClient"/>.
/// Talks to the Google Drive Activity API v2 and the Admin Directory API
/// using the configured service account. This is the only file that imports
/// <c>Google.Apis.*</c> for Drive Activity monitoring; the Application-layer
/// <c>DriveActivityMonitorService</c> never sees SDK types.
/// </summary>
public sealed class GoogleDriveActivityClient : IGoogleDriveActivityClient
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleDriveActivityClient> _logger;

    private DriveActivityService? _activityService;
    private DirectoryService? _directoryService;
    private string? _serviceAccountEmail;
    private string? _serviceAccountClientId;
    private bool _serviceAccountEmailResolved;
    private bool _serviceAccountClientIdResolved;

    public GoogleDriveActivityClient(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleDriveActivityClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_settings.ServiceAccountKeyPath) ||
        !string.IsNullOrEmpty(_settings.ServiceAccountKeyJson);

    public async Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        if (_serviceAccountEmailResolved && _serviceAccountEmail is not null)
        {
            return _serviceAccountEmail;
        }

        var json = await GetServiceAccountJsonAsync(ct);
        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var emailElement))
            {
                _serviceAccountEmail = emailElement.GetString();
            }
        }

        _serviceAccountEmail ??= "unknown@serviceaccount.iam.gserviceaccount.com";
        _serviceAccountEmailResolved = true;
        return _serviceAccountEmail;
    }

    public async Task<string?> GetServiceAccountClientIdAsync(CancellationToken ct = default)
    {
        if (_serviceAccountClientIdResolved)
        {
            return _serviceAccountClientId;
        }

        var json = await GetServiceAccountJsonAsync(ct);
        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_id", out var clientIdElement))
            {
                _serviceAccountClientId = clientIdElement.GetString();
            }
        }

        _serviceAccountClientIdResolved = true;
        return _serviceAccountClientId;
    }

    public async IAsyncEnumerable<DriveActivityEvent> QueryActivityAsync(
        string googleItemId,
        string sinceIsoTimestamp,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activityService = await GetActivityServiceAsync();
        string? pageToken = null;

        do
        {
            var request = new QueryDriveActivityRequest
            {
                ItemName = $"items/{googleItemId}",
                Filter = $"time >= \"{sinceIsoTimestamp}\"",
                PageSize = 100,
                PageToken = pageToken,
            };

            QueryDriveActivityResponse response;
            try
            {
                response = await activityService.Activity.Query(request).ExecuteAsync(ct);
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
            {
                throw new DriveActivityResourceNotFoundException(googleItemId);
            }

            if (response.Activities is not null)
            {
                foreach (var activity in response.Activities)
                {
                    yield return Map(activity);
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    public async Task<string?> TryResolvePersonEmailAsync(string peopleId, CancellationToken ct = default)
    {
        if (!peopleId.StartsWith("people/", StringComparison.Ordinal))
        {
            // Already an email, no lookup needed.
            return peopleId;
        }

        try
        {
            var directoryService = await GetDirectoryServiceAsync();
            var userId = peopleId["people/".Length..];
            var user = await directoryService.Users.Get(userId).ExecuteAsync(ct);
            return user?.PrimaryEmail;
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code is 404 or 403)
        {
            _logger.LogDebug("Directory API could not resolve {PeopleId} (HTTP {Code})",
                peopleId, ex.Error.Code);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolving {PeopleId} via Directory API", peopleId);
            return null;
        }
    }

    private static DriveActivityEvent Map(DriveActivity activity)
    {
        var actors = new List<DriveActivityActor>();
        if (activity.Actors is not null)
        {
            foreach (var actor in activity.Actors)
            {
                actors.Add(new DriveActivityActor(
                    KnownUserPersonName: actor.User?.KnownUser?.PersonName,
                    IsAdministrator: actor.Administrator is not null,
                    IsSystem: actor.System is not null));
            }
        }

        DriveActivityPermissionChange? permissionChange = null;
        var permChange = activity.PrimaryActionDetail?.PermissionChange;
        if (permChange is not null)
        {
            permissionChange = new DriveActivityPermissionChange(
                AddedPermissions: MapPermissions(permChange.AddedPermissions),
                RemovedPermissions: MapPermissions(permChange.RemovedPermissions));
        }

        return new DriveActivityEvent(actors, permissionChange);
    }

    private static IReadOnlyList<DriveActivityPermission> MapPermissions(IList<Permission>? source)
    {
        if (source is null || source.Count == 0)
        {
            return Array.Empty<DriveActivityPermission>();
        }

        var result = new List<DriveActivityPermission>(source.Count);
        foreach (var p in source)
        {
            result.Add(new DriveActivityPermission(
                Role: p.Role,
                UserPersonName: p.User?.KnownUser?.PersonName,
                GroupEmail: p.Group?.Email,
                DomainName: p.Domain?.Name,
                IsAnyone: p.Anyone is not null));
        }
        return result;
    }

    private async Task<DriveActivityService> GetActivityServiceAsync()
    {
        if (_activityService is not null)
        {
            return _activityService;
        }

        var credential = await GetCredentialAsync(DriveActivityService.Scope.DriveActivityReadonly);

        _activityService = new DriveActivityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans",
        });

        return _activityService;
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService is not null)
        {
            return _directoryService;
        }

        var credential = await GetCredentialAsync(DirectoryService.Scope.AdminDirectoryUserReadonly);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans",
        });

        return _directoryService;
    }

    private async Task<GoogleCredential> GetCredentialAsync(params string[] scopes)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            await using var stream = System.IO.File.OpenRead(_settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. Set ServiceAccountKeyPath or ServiceAccountKeyJson.");
        }

        return credential.CreateScoped(scopes);
    }

    private async Task<string?> GetServiceAccountJsonAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            return _settings.ServiceAccountKeyJson;
        }

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            return await System.IO.File.ReadAllTextAsync(_settings.ServiceAccountKeyPath, ct);
        }

        return null;
    }
}
