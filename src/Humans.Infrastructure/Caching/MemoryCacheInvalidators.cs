using Microsoft.Extensions.Caching.Memory;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Teams;

namespace Humans.Infrastructure.Caching;

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementations of the cross-cutting
/// invalidator interfaces in <c>Humans.Application.Interfaces.Caching</c>.
/// Thin wrappers around the existing extension methods in
/// <c>MemoryCacheExtensions</c> — exist so services/decorators in the
/// Application layer can describe their cross-section cache dependencies
/// without coupling directly to <c>IMemoryCache</c>.
/// </summary>
public sealed class NavBadgeCacheInvalidator(IMemoryCache cache) : INavBadgeCacheInvalidator
{
    public void Invalidate() => cache.InvalidateNavBadgeCounts();
}

public sealed class NotificationMeterCacheInvalidator(IMemoryCache cache) : INotificationMeterCacheInvalidator
{
    public void Invalidate() => cache.InvalidateNotificationMeters();
}

public sealed class VotingBadgeCacheInvalidator(IMemoryCache cache) : IVotingBadgeCacheInvalidator
{
    public void Invalidate(Guid userId) => cache.InvalidateVotingBadge(userId);
}

public sealed class IssuesBadgeCacheInvalidator(IMemoryCache cache) : IIssuesBadgeCacheInvalidator
{
    public void Invalidate(Guid userId) => cache.InvalidateIssuesBadge(userId);
    public void InvalidateMany(IEnumerable<Guid> userIds)
    {
        foreach (var id in userIds) cache.InvalidateIssuesBadge(id);
    }
}

public sealed class CampLeadJoinRequestsBadgeCacheInvalidator(IMemoryCache cache)
    : ICampLeadJoinRequestsBadgeCacheInvalidator
{
    public void Invalidate(Guid userId) => cache.InvalidateCampLeadJoinRequestsBadge(userId);
}

public sealed class RoleAssignmentClaimsCacheInvalidator(IMemoryCache cache) : IRoleAssignmentClaimsCacheInvalidator
{
    public void Invalidate(Guid userId) => cache.InvalidateRoleAssignmentClaims(userId);
}

public sealed class ActiveTeamsCacheInvalidator(ITeamService teamService) : IActiveTeamsCacheInvalidator
{
    public void Invalidate() => teamService.InvalidateActiveTeamsCache();
}
