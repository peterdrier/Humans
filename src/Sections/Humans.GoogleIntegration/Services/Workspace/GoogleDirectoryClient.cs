using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Services;
using Humans.Base.Extensions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;

namespace Humans.GoogleIntegration.Services.Workspace;

internal sealed class GoogleDirectoryClient(
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<GoogleDirectoryClient> logger) : IGoogleDirectoryClient
{
    private readonly GoogleWorkspaceSettings _settings = settings.Value;

    private DirectoryService? _directoryService;

    public async Task<DirectoryUserListResult> ListDomainUsersAsync(CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
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
            logger.LogWarning(ex,
                "Google API error listing domain users: Code={Code} Message={Message}",
                ex.Error?.Code, ex.Error?.Message);
            return new DirectoryUserListResult(
                Users: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<DirectoryGroupListResult> ListDomainGroupsAsync(CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
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
                        // Downstream treats both as non-nullable; Google always returns them on list in practice.
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
            logger.LogWarning(ex,
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
