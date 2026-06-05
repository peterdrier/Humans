using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Web.Authorization;

/// <summary>
/// Syncs active role assignments to Identity role claims and adds membership
/// status claims. Runs per authenticated request; cached 60s per user.
/// </summary>
public class RoleAssignmentClaimsTransformation(
    IRoleAssignmentService roleAssignments,
    ITeamServiceRead teams,
    IUserServiceRead userService,
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
        if (principal.HasClaim(c => string.Equals(c.Type, ClaimsAddedMarkerType, StringComparison.Ordinal)
            && string.Equals(c.Value, ActiveClaimValue, StringComparison.Ordinal)))
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
        var claims = new List<Claim>();

        // Cached UserInfo read-model avoids hitting profiles on every authenticated request.
        var userInfo = await userService.GetUserInfoAsync(userId);
        var isSuspended = userInfo?.IsSuspended ?? false;
        var hasProfile = userInfo?.HasProfile ?? false;

        var activeRoles = await roleAssignments.GetActiveForUserAsync(userId);
        foreach (var role in activeRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.RoleName));
        }

        if (!isSuspended)
        {
            // Warm CachingTeamService index; no DB round-trip; returns active memberships only.
            var allTeams = await teams.GetTeamsAsync();
            var volunteersTeam = allTeams.GetValueOrDefault(SystemTeamIds.Volunteers);
            var isVolunteerMember = volunteersTeam?.Members.Any(m => m.UserId == userId) == true;
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
