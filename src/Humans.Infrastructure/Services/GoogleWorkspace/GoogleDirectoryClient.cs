using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Services;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IGoogleDirectoryClient"/>.
/// Talks to the Google Workspace Admin Directory API (Users.List and
/// Groups.List) using the configured service account. This is the only file
/// that imports <c>Google.Apis.*</c> for domain-wide enumeration performed
/// by <c>GoogleWorkspaceSyncService</c>; the Application-layer sync service
/// (coming in §15 Part 2b) never sees SDK types.
/// </summary>
public sealed class GoogleDirectoryClient : IGoogleDirectoryClient
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleDirectoryClient> _logger;

    private DirectoryService? _directoryService;

    public GoogleDirectoryClient(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleDirectoryClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DirectoryUserListResult> ListDomainUsersAsync(CancellationToken ct = default)
    {
        try
        {
            var service = await GetDirectoryServiceAsync(ct);
            var users = new List<DirectoryUser>();
            string? pageToken = null;

            do
            {
                var request = service.Users.List();
                request.Domain = _settings.Domain;
                request.MaxResults = 500;
                if (pageToken is not null)
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(ct);

                if (response.UsersValue is not null)
                {
                    foreach (var u in response.UsersValue)
                    {
                        if (!string.IsNullOrEmpty(u.PrimaryEmail))
                        {
                            users.Add(new DirectoryUser(u.PrimaryEmail));
                        }
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return new DirectoryUserListResult(users, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error listing domain users: Code={Code} Message={Message}",
                ex.Error?.Code, ex.Error?.Message);
            return new DirectoryUserListResult(
                Users: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<DirectoryGroupListResult> ListDomainGroupsAsync(CancellationToken ct = default)
    {
        try
        {
            var service = await GetDirectoryServiceAsync(ct);
            var groups = new List<DirectoryGroup>();
            string? pageToken = null;

            do
            {
                var request = service.Groups.List();
                request.Domain = _settings.Domain;
                request.MaxResults = 200;
                if (pageToken is not null)
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(ct);

                if (response.GroupsValue is not null)
                {
                    foreach (var g in response.GroupsValue)
                    {
                        // Require both Id and Email to be populated so downstream
                        // callers can treat them as non-nullable. Defensive —
                        // in practice Google always returns both on list.
                        if (string.IsNullOrEmpty(g.Email) || string.IsNullOrEmpty(g.Id))
                        {
                            continue;
                        }

                        groups.Add(new DirectoryGroup(
                            Id: g.Id,
                            Email: g.Email,
                            DisplayName: g.Name,
                            DirectMembersCount: g.DirectMembersCount));
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return new DirectoryGroupListResult(groups, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error listing domain groups: Code={Code} Message={Message}",
                ex.Error?.Code, ex.Error?.Message);
            return new DirectoryGroupListResult(
                Groups: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync(CancellationToken ct)
    {
        if (_directoryService is not null)
        {
            return _directoryService;
        }

        var credential = await GoogleCredentialLoader.LoadScopedAsync(
            _settings, ct,
            DirectoryService.Scope.AdminDirectoryUserReadonly,
            DirectoryService.Scope.AdminDirectoryGroupReadonly);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
    }
}
