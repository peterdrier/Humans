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
/// Syncs active RoleAssignment entities to Identity role claims and adds membership status claims.
/// Runs per authenticated request; cached 60s per user. See HUM0014 / #750.
/// </summary>
[Grandfathered(
    "HUM0014",
    "Auth claims transformation runs on every authenticated request and reads role_assignments via IRoleAssignmentRepository directly. Routing through IRoleAssignmentService drags in INotificationEmitter / ISystemTeamSync / IGoogleSyncService / Hangfire scheduler — wrong for the request-time auth hot path and unresolvable in the integration-test host. Team membership uses the cache-backed ITeamService. A thin Application-layer read-only interface is the proper home — tracked separately.",
    "2026-05-17",
    "nobodies-collective/Humans#750")]
public class RoleAssignmentClaimsTransformation(
    IRoleAssignmentRepository roleAssignments,
    ITeamService teams,
    IUserService userService,
    IClock clock,
    IMemoryCache cache) : IClaimsTransformation
{
    /// <summary>Active member of the Volunteers team.</summary>
    public const string ActiveMemberClaimType = "ActiveMember";

    /// <summary>User has a profile record. Lets MembershipRequiredFilter separate profileless accounts from onboarding members.</summary>
    public const string HasProfileClaimType = "HasProfile";

    public const string ActiveClaimValue = "true";
    public const string ClaimsAddedMarkerType = "RoleAssignmentClaimsAdded";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

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

        // Skip duplicate claims on repeat calls within the same request.
        if (principal.HasClaim(c => string.Equals(c.Type, ClaimsAddedMarkerType, StringComparison.Ordinal) && string.Equals(c.Value, ActiveClaimValue, StringComparison.Ordinal)))
        {
            return principal;
        }

        var claims = await cache.GetOrCreateAsync(CacheKeys.RoleAssignmentClaims(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await LoadClaimsAsync(userId);
        }) ?? [];

        var identity = new ClaimsIdentity();
        foreach (var claim in claims)
        {
            identity.AddClaim(claim);
        }

        identity.AddClaim(new Claim(ClaimsAddedMarkerType, ActiveClaimValue));

        principal.AddIdentity(identity);

        return principal;
    }

    private async Task<List<Claim>> LoadClaimsAsync(Guid userId)
    {
        var now = clock.GetCurrentInstant();
        var claims = new List<Claim>();

        // Cached UserInfo read-model avoids hitting profiles on every authenticated request.
        var userInfo = await userService.GetUserInfoAsync(userId);
        var isSuspended = userInfo?.IsSuspended ?? false;
        var hasProfile = userInfo?.HasProfile ?? false;

        var activeRoles = await roleAssignments.GetActiveRoleNamesAsync(userId, now);
        foreach (var role in activeRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!isSuspended)
        {
            // Warm CachingTeamService index — no DB round-trip; returns active memberships only.
            var memberships = await teams.GetUserTeamsAsync(userId);
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
