using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Services;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IGoogleGroupMembershipClient"/>.
/// Talks to the Google Workspace Admin SDK <b>Directory</b> Members API using the
/// configured service account. The Directory API (unlike the Cloud Identity Groups
/// API) can add members whose email is <i>not</i> a Google account — external
/// addresses such as <c>someone@hotmail.fr</c> — directly as group members
/// ("a member can be a user or another group ... inside or outside of your
/// account's domains"). The Cloud Identity <c>memberships.create</c> call requires
/// the member key to resolve to an existing Google identity and returns a 403
/// (Error 2028, "Permission denied for resource groups/… (or it may not exist)")
/// for unresolvable external emails, which is why membership operations run
/// through Directory here.
/// </summary>
/// <remarks>
/// The membership "resource name" exchanged with callers keeps the
/// <c>groups/{groupId}/memberships/{memberId}</c> shape (matching
/// <see cref="StubGoogleGroupMembershipClient"/>) so the sync service's
/// list-then-delete round-trip is unchanged. It is never persisted — it lives
/// only for the duration of a single reconciliation pass.
/// </remarks>
public sealed class GoogleGroupMembershipClient(
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<GoogleGroupMembershipClient> logger) : IGoogleGroupMembershipClient
{
    private readonly GoogleWorkspaceSettings _settings = settings.Value;

    private DirectoryService? _directoryService;

    public async Task<GroupMembershipListResult> ListMembershipsAsync(
        string groupGoogleId,
        CancellationToken ct = default)
    {
        var memberships = new List<GroupMembership>();
        string? pageToken = null;

        try
        {
            var directory = await GetDirectoryServiceAsync(ct);
            do
            {
                var request = directory.Members.List(groupGoogleId);
                request.MaxResults = 200;
                if (pageToken is not null)
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(ct);

                if (response.MembersValue is not null)
                {
                    foreach (var m in response.MembersValue)
                    {
                        memberships.Add(new GroupMembership(
                            MemberEmail: m.Email,
                            ResourceName: $"groups/{groupGoogleId}/memberships/{m.Email}"));
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return new GroupMembershipListResult(memberships, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            logger.LogWarning(ex,
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
            var directory = await GetDirectoryServiceAsync(ct);
            var member = new Member
            {
                Email = memberEmail,
                Role = "MEMBER"
            };

            await directory.Members
                .Insert(member, groupGoogleId)
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
            logger.LogWarning(ex,
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
        // Resource name shape: groups/{groupKey}/memberships/{memberKey}.
        // The Directory delete needs the group and member keys separately.
        var parts = membershipResourceName.Split('/');
        if (parts.Length != 4
            || !string.Equals(parts[0], "groups", StringComparison.Ordinal)
            || !string.Equals(parts[2], "memberships", StringComparison.Ordinal))
        {
            return new GoogleClientError(0,
                $"Malformed membership resource name '{membershipResourceName}'");
        }

        var groupKey = parts[1];
        var memberKey = parts[3];

        try
        {
            var directory = await GetDirectoryServiceAsync(ct);
            await directory.Members.Delete(groupKey, memberKey).ExecuteAsync(ct);
            return null;
        }
        catch (Google.GoogleApiException ex)
        {
            logger.LogWarning(ex,
                "Google API error deleting membership {Name}: Code={Code} Message={Message}",
                membershipResourceName, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync(CancellationToken ct)
    {
        if (_directoryService is not null)
        {
            return _directoryService;
        }

        var credential = await GoogleCredentialLoader
            .LoadScopedAsync(_settings, ct, DirectoryService.Scope.AdminDirectoryGroupMember);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
    }
}
