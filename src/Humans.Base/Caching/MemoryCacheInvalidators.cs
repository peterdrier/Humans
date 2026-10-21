using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Extensions;
using Humans.Base.Interfaces.Caching;

namespace Humans.Base.Caching;

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementations of the cross-cutting
/// invalidator interfaces in <c>Humans.Base.Interfaces.Caching</c>.
/// Thin wrappers around the existing extension methods in
/// <c>MemoryCacheExtensions</c> — exist so services/decorators in the
/// Application layer can describe their cross-section cache dependencies
/// without coupling directly to <c>IMemoryCache</c>.
/// </summary>
public sealed class NotificationMeterCacheInvalidator(IMemoryCache cache) : INotificationMeterCacheInvalidator
{
    public void Invalidate() => cache.InvalidateNotificationMeters();
}

public sealed class RoleAssignmentClaimsCacheInvalidator(IMemoryCache cache) : IRoleAssignmentClaimsCacheInvalidator
{
    public void Invalidate(Guid userId) => cache.InvalidateRoleAssignmentClaims(userId);
}

// ActiveTeamsCacheInvalidator is deliberately NOT here: it injects ITeamService, so it
// cannot live in Base. It moved to Humans.Teams/Services/ActiveTeamsCacheInvalidator.cs
// with its namespace preserved (nobodies-collective/Humans#866, G5 lane 3a-1).
