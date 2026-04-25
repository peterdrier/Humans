using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Dev/test <see cref="IGoogleGroupMembershipClient"/> that keeps an in-memory
/// map of group memberships so the Application-layer sync service can be
/// exercised locally without a Google service account. Mirrors the
/// idempotency contract of the real client (409 on duplicate add becomes
/// <see cref="GroupMembershipMutationOutcome.AlreadyExists"/>). Per the §15
/// connector pattern, the Application-layer service runs against this stub —
/// there is no "stub service" variant.
/// </summary>
public sealed class StubGoogleGroupMembershipClient : IGoogleGroupMembershipClient
{
    private readonly ILogger<StubGoogleGroupMembershipClient> _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _membersByGroup = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _nextMembershipId = 1;

    public StubGoogleGroupMembershipClient(ILogger<StubGoogleGroupMembershipClient> logger)
    {
        _logger = logger;
    }

    public Task<GroupMembershipListResult> ListMembershipsAsync(
        string groupGoogleId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] List memberships for group {GroupId}", groupGoogleId);

        lock (_gate)
        {
            if (!_membersByGroup.TryGetValue(groupGoogleId, out var members))
            {
                return Task.FromResult(new GroupMembershipListResult(
                    Memberships: Array.Empty<GroupMembership>(),
                    Error: null));
            }

            var snapshot = members
                .Select(kvp => new GroupMembership(kvp.Key, kvp.Value))
                .ToList();
            return Task.FromResult(new GroupMembershipListResult(snapshot, Error: null));
        }
    }

    public Task<GroupMembershipMutationResult> CreateMembershipAsync(
        string groupGoogleId,
        string memberEmail,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Add {Email} to group {GroupId}", memberEmail, groupGoogleId);

        lock (_gate)
        {
            if (!_membersByGroup.TryGetValue(groupGoogleId, out var members))
            {
                members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _membersByGroup[groupGoogleId] = members;
            }

            if (members.ContainsKey(memberEmail))
            {
                return Task.FromResult(new GroupMembershipMutationResult(
                    GroupMembershipMutationOutcome.AlreadyExists, Error: null));
            }

            var resourceName = $"groups/{groupGoogleId}/memberships/stub-{_nextMembershipId++}";
            members[memberEmail] = resourceName;
            return Task.FromResult(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Added, Error: null));
        }
    }

    public Task<GoogleClientError?> DeleteMembershipAsync(
        string membershipResourceName,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Delete membership {Name}", membershipResourceName);

        lock (_gate)
        {
            foreach (var (_, members) in _membersByGroup)
            {
                var keyToRemove = members
                    .FirstOrDefault(kvp => string.Equals(kvp.Value, membershipResourceName, StringComparison.Ordinal))
                    .Key;
                if (keyToRemove is not null)
                {
                    members.Remove(keyToRemove);
                    return Task.FromResult<GoogleClientError?>(null);
                }
            }
        }

        // Not found — mirror Google's 404 behavior.
        return Task.FromResult<GoogleClientError?>(new GoogleClientError(404, "membership not found"));
    }
}
