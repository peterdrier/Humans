using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Application;
using Humans.Application.Architecture;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

/// <summary>
/// Claims transformation that syncs active RoleAssignment entities to Identity role claims
/// and adds membership status claims. Runs on every authenticated request.
/// Results are cached per user for 60 seconds to avoid 2 DB queries per request.
/// </summary>
/// <remarks>
/// Reads role assignments via <see cref="IRoleAssignmentRepository"/> directly
/// (HUM0014 grandfathered) instead of <see cref="Application.Interfaces.Auth.IRoleAssignmentService"/>.
/// The service carries heavy transitive dependencies (notification emitter,
/// system-team sync, Hangfire scheduler) that don't belong on the request-time
/// auth hot path — and those dependencies are unresolvable in the integration-
/// test host where Hangfire storage is intentionally not initialized. Team
/// membership goes through the cache-backed <see cref="ITeamService"/>.
/// Issue #750 narrowed the surface to one read-only repository method;
/// promoting it to a thin Application-layer interface is a separate cleanup.
/// </remarks>
[Grandfathered(
    "HUM0014",
    "Auth claims transformation runs on every authenticated request and reads role_assignments via IRoleAssignmentRepository directly. Routing through IRoleAssignmentService drags in INotificationEmitter / ISystemTeamSync / IGoogleSyncService / Hangfire scheduler — wrong for the request-time auth hot path and unresolvable in the integration-test host. Team membership uses the cache-backed ITeamService. A thin Application-layer read-only interface is the proper home — tracked separately.",
    "2026-05-17",
    "nobodies-collective/Humans#750")]
public class RoleAssignmentClaimsTransformation : IClaimsTransformation
{
    /// <summary>
    /// Claim type indicating the user is an active member of the Volunteers team.
    /// </summary>
    public const string ActiveMemberClaimType = "ActiveMember";

    /// <summary>
    /// Claim type indicating the user has a profile record.
    /// Used by MembershipRequiredFilter to distinguish profileless accounts from onboarding members.
    /// </summary>
    public const string HasProfileClaimType = "HasProfile";

    public const string ActiveClaimValue = "true";
    public const string ClaimsAddedMarkerType = "RoleAssignmentClaimsAdded";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IRoleAssignmentRepository _roleAssignments;
    private readonly ITeamService _teams;
    private readonly IUserService _userService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    public RoleAssignmentClaimsTransformation(
        IRoleAssignmentRepository roleAssignments,
        ITeamService teams,
        IUserService userService,
        IClock clock,
        IMemoryCache cache)
    {
        _roleAssignments = roleAssignments;
        _teams = teams;
        _userService = userService;
        _clock = clock;
        _cache = cache;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return principal;
        }

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return principal;
        }

        // Avoid adding duplicate role claims on subsequent calls within the same request
        if (principal.HasClaim(c => string.Equals(c.Type, ClaimsAddedMarkerType, StringComparison.Ordinal) && string.Equals(c.Value, ActiveClaimValue, StringComparison.Ordinal)))
        {
            return principal;
        }

        var claims = await _cache.GetOrCreateAsync(CacheKeys.RoleAssignmentClaims(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await LoadClaimsAsync(userId);
        }) ?? [];

        var identity = new ClaimsIdentity();
        foreach (var claim in claims)
        {
            identity.AddClaim(claim);
        }

        // Marker claim to prevent duplicate processing
        identity.AddClaim(new Claim(ClaimsAddedMarkerType, ActiveClaimValue));

        principal.AddIdentity(identity);

        return principal;
    }

    private async Task<List<Claim>> LoadClaimsAsync(Guid userId)
    {
        var now = _clock.GetCurrentInstant();
        var claims = new List<Claim>();

        // Suspension + profile-presence both come from the cached UserInfo
        // read-model so we don't hit the profiles table on every authenticated
        // request. Null userInfo (user row not yet loaded / not in cache) is
        // treated as "no profile, not suspended" — same observable behavior as
        // the prior dbContext.Profiles.AnyAsync calls returning false.
        var userInfo = await _userService.GetUserInfoAsync(userId);
        var isSuspended = userInfo?.IsSuspended ?? false;
        var hasProfile = userInfo?.HasProfile ?? false;

        var activeRoles = await _roleAssignments.GetActiveRoleNamesAsync(userId, now);
        foreach (var role in activeRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!isSuspended)
        {
            // GetUserTeamsAsync hits the warm in-memory team-membership index
            // owned by CachingTeamService — no DB round-trip on hot path. The
            // cache returns only active (LeftAt is null) memberships.
            var memberships = await _teams.GetUserTeamsAsync(userId);
            var isVolunteerMember = memberships.Any(m => m.TeamId == SystemTeamIds.Volunteers);
            if (isVolunteerMember)
            {
                claims.Add(new Claim(ActiveMemberClaimType, ActiveClaimValue));
            }
        }

        if (hasProfile)
        {
            claims.Add(new Claim(HasProfileClaimType, ActiveClaimValue));
        }

        return claims;
    }
}
