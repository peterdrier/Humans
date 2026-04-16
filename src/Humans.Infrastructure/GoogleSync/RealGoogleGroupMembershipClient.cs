using Google.Apis.CloudIdentity.v1;
using Google.Apis.CloudIdentity.v1.Data;

namespace Humans.Infrastructure.GoogleSync;

/// <summary>
/// Production <see cref="IGoogleGroupMembershipClient"/> that delegates to a
/// <see cref="CloudIdentityService"/>. Pagination is handled here so callers can treat
/// <see cref="ListMembershipsAsync"/> as a single read.
/// </summary>
internal sealed class RealGoogleGroupMembershipClient : IGoogleGroupMembershipClient
{
    private readonly CloudIdentityService _service;

    public RealGoogleGroupMembershipClient(CloudIdentityService service)
    {
        _service = service;
    }

    public async Task<IReadOnlyList<Membership>> ListMembershipsAsync(string groupId, CancellationToken cancellationToken)
    {
        var result = new List<Membership>();
        string? pageToken = null;
        do
        {
            var request = _service.Groups.Memberships.List($"groups/{groupId}");
            request.PageSize = 200;
            if (pageToken is not null)
                request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken);
            if (response.Memberships is not null)
                result.AddRange(response.Memberships);

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    public async Task CreateMembershipAsync(string groupId, string userEmail, CancellationToken cancellationToken)
    {
        var membership = new Membership
        {
            PreferredMemberKey = new EntityKey { Id = userEmail },
            Roles = [new MembershipRole { Name = "MEMBER" }]
        };

        await _service.Groups.Memberships
            .Create(membership, $"groups/{groupId}")
            .ExecuteAsync(cancellationToken);
    }

    public async Task DeleteMembershipAsync(string membershipName, CancellationToken cancellationToken)
    {
        await _service.Groups.Memberships.Delete(membershipName)
            .ExecuteAsync(cancellationToken);
    }
}
