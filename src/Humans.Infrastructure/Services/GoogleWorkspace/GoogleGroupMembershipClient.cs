using Google.Apis.Auth.OAuth2;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.CloudIdentity.v1.Data;
using Google.Apis.Services;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IGoogleGroupMembershipClient"/>.
/// Talks to the Cloud Identity Groups Memberships API using the configured
/// service account. This is the only file that imports <c>Google.Apis.*</c>
/// for group-membership operations; the Application-layer sync service
/// (coming in §15 Part 2b) never sees SDK types.
/// </summary>
public sealed class GoogleGroupMembershipClient : IGoogleGroupMembershipClient
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleGroupMembershipClient> _logger;

    private CloudIdentityService? _cloudIdentityService;

    public GoogleGroupMembershipClient(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleGroupMembershipClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<GroupMembershipListResult> ListMembershipsAsync(
        string groupGoogleId,
        CancellationToken ct = default)
    {
        var memberships = new List<GroupMembership>();
        string? pageToken = null;

        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            do
            {
                var request = cloudIdentity.Groups.Memberships.List($"groups/{groupGoogleId}");
                request.PageSize = 200;
                if (pageToken is not null)
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(ct);

                if (response.Memberships is not null)
                {
                    foreach (var m in response.Memberships)
                    {
                        memberships.Add(new GroupMembership(
                            MemberEmail: m.PreferredMemberKey?.Id,
                            ResourceName: m.Name));
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return new GroupMembershipListResult(memberships, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error listing memberships for group {GroupId}: Code={Code} Message={Message}",
                groupGoogleId, ex.Error?.Code, ex.Error?.Message);
            return new GroupMembershipListResult(
                Memberships: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GroupMembershipMutationResult> CreateMembershipAsync(
        string groupGoogleId,
        string memberEmail,
        CancellationToken ct = default)
    {
        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            var membership = new Membership
            {
                PreferredMemberKey = new EntityKey { Id = memberEmail },
                Roles = [new MembershipRole { Name = "MEMBER" }]
            };

            await cloudIdentity.Groups.Memberships
                .Create(membership, $"groups/{groupGoogleId}")
                .ExecuteAsync(ct);

            return new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Added,
                Error: null);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 409)
        {
            // Treat as idempotent success — the membership already exists.
            return new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.AlreadyExists,
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error adding {Email} to group {GroupId}: Code={Code} Message={Message}",
                memberEmail, groupGoogleId, ex.Error?.Code, ex.Error?.Message);
            return new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GoogleClientError?> DeleteMembershipAsync(
        string membershipResourceName,
        CancellationToken ct = default)
    {
        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            await cloudIdentity.Groups.Memberships.Delete(membershipResourceName).ExecuteAsync(ct);
            return null;
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error deleting membership {Name}: Code={Code} Message={Message}",
                membershipResourceName, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    private async Task<CloudIdentityService> GetCloudIdentityServiceAsync(CancellationToken ct)
    {
        if (_cloudIdentityService is not null)
        {
            return _cloudIdentityService;
        }

        var credential = await GoogleCredentialLoader
            .LoadScopedAsync(_settings, ct, CloudIdentityService.Scope.CloudIdentityGroups);

        _cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _cloudIdentityService;
    }
}
