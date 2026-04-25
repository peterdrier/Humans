using Microsoft.Extensions.Caching.Memory;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;

namespace Humans.Infrastructure.Caching;

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementations of the cross-cutting
/// invalidator interfaces in <c>Humans.Application.Interfaces.Caching</c>.
/// Thin wrappers around the existing extension methods in
/// <c>MemoryCacheExtensions</c> — exist so services/decorators in the
/// Application layer can describe their cross-section cache dependencies
/// without coupling directly to <c>IMemoryCache</c>.
/// </summary>
public sealed class NavBadgeCacheInvalidator : INavBadgeCacheInvalidator
{
    private readonly IMemoryCache _cache;
    public NavBadgeCacheInvalidator(IMemoryCache cache) => _cache = cache;
    public void Invalidate() => _cache.InvalidateNavBadgeCounts();
}

public sealed class NotificationMeterCacheInvalidator : INotificationMeterCacheInvalidator
{
    private readonly IMemoryCache _cache;
    public NotificationMeterCacheInvalidator(IMemoryCache cache) => _cache = cache;
    public void Invalidate() => _cache.InvalidateNotificationMeters();
}

public sealed class VotingBadgeCacheInvalidator : IVotingBadgeCacheInvalidator
{
    private readonly IMemoryCache _cache;
    public VotingBadgeCacheInvalidator(IMemoryCache cache) => _cache = cache;
    public void Invalidate(Guid userId) => _cache.InvalidateVotingBadge(userId);
}

public sealed class RoleAssignmentClaimsCacheInvalidator : IRoleAssignmentClaimsCacheInvalidator
{
    private readonly IMemoryCache _cache;
    public RoleAssignmentClaimsCacheInvalidator(IMemoryCache cache) => _cache = cache;
    public void Invalidate(Guid userId) => _cache.InvalidateRoleAssignmentClaims(userId);
}

public sealed class ActiveTeamsCacheInvalidator : IActiveTeamsCacheInvalidator
{
    private readonly IMemoryCache _cache;
    public ActiveTeamsCacheInvalidator(IMemoryCache cache) => _cache = cache;
    public void Invalidate() => _cache.InvalidateActiveTeams();
}
