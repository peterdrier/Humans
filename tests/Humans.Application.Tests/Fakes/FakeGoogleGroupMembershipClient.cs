using System.Net;
using Google;
using Google.Apis.CloudIdentity.v1.Data;
using Google.Apis.Requests;
using Humans.Infrastructure.GoogleSync;

namespace Humans.Application.Tests.Fakes;

/// <summary>
/// In-memory fake for <see cref="IGoogleGroupMembershipClient"/> used by reconciliation tests.
/// State is keyed by the Google Group id (not the full <c>groups/{id}</c> resource name).
/// </summary>
internal sealed class FakeGoogleGroupMembershipClient : IGoogleGroupMembershipClient
{
    private readonly Dictionary<string, List<Membership>> _state = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failingGroups = new(StringComparer.Ordinal);

    public IReadOnlyList<Membership> GetMemberships(string groupId) =>
        _state.TryGetValue(groupId, out var list)
            ? list.ToList()
            : [];

    /// <summary>
    /// Seeds an existing membership on the given group. Uses a deterministic membership
    /// resource name so tests can assert against it.
    /// </summary>
    public void SeedMembership(string groupId, string email)
    {
        if (!_state.TryGetValue(groupId, out var list))
        {
            list = [];
            _state[groupId] = list;
        }

        list.Add(new Membership
        {
            Name = $"groups/{groupId}/memberships/{Guid.NewGuid():N}",
            PreferredMemberKey = new EntityKey { Id = email }
        });
    }

    /// <summary>
    /// Registers a group id whose List/Create/Delete operations all throw a generic exception.
    /// Used to exercise the error-path branches in SyncGroupResourceAsync and the post-execute
    /// softDeletedTeamIds filter.
    /// </summary>
    public void FailGroup(string groupId) => _failingGroups.Add(groupId);

    public Task<IReadOnlyList<Membership>> ListMembershipsAsync(string groupId, CancellationToken cancellationToken)
    {
        if (_failingGroups.Contains(groupId))
            throw BuildApiException(HttpStatusCode.NotFound, $"Group {groupId} not found.");

        return Task.FromResult<IReadOnlyList<Membership>>(GetMemberships(groupId));
    }

    public Task CreateMembershipAsync(string groupId, string userEmail, CancellationToken cancellationToken)
    {
        if (_failingGroups.Contains(groupId))
            throw BuildApiException(HttpStatusCode.Forbidden, "Simulated failure on CreateMembership.");

        if (!_state.TryGetValue(groupId, out var list))
        {
            list = [];
            _state[groupId] = list;
        }

        var existing = list.FirstOrDefault(m =>
            string.Equals(m.PreferredMemberKey?.Id, userEmail, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            throw BuildApiException((HttpStatusCode)409, $"{userEmail} is already a member.");

        list.Add(new Membership
        {
            Name = $"groups/{groupId}/memberships/{Guid.NewGuid():N}",
            PreferredMemberKey = new EntityKey { Id = userEmail }
        });
        return Task.CompletedTask;
    }

    public Task DeleteMembershipAsync(string membershipName, CancellationToken cancellationToken)
    {
        // membershipName format: "groups/{groupId}/memberships/{membershipId}"
        var parts = membershipName.Split('/');
        if (parts.Length < 4
            || !string.Equals(parts[0], "groups", StringComparison.Ordinal)
            || !string.Equals(parts[2], "memberships", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Malformed membership name: {membershipName}", nameof(membershipName));
        }

        var groupId = parts[1];
        if (_failingGroups.Contains(groupId))
            throw BuildApiException(HttpStatusCode.Forbidden, "Simulated failure on DeleteMembership.");

        if (!_state.TryGetValue(groupId, out var list))
            return Task.CompletedTask;

        list.RemoveAll(m => string.Equals(m.Name, membershipName, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static GoogleApiException BuildApiException(HttpStatusCode code, string message)
    {
        var error = new RequestError
        {
            Code = (int)code,
            Message = message
        };
        return new GoogleApiException("CloudIdentity", message)
        {
            Error = error,
            HttpStatusCode = code
        };
    }
}
