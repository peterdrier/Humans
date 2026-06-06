using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Web.Authorization;

/// <summary>
/// Syncs active role assignments to Identity role claims and stamps the user's stored
/// <see cref="UserState"/> as a claim, the single source of truth for app access.
/// Runs per authenticated request; cached 60s per user.
/// </summary>
public class RoleAssignmentClaimsTransformation(
    IRoleAssignmentService roleAssignments,
    IUserServiceRead userService,
    IMemoryCache cache) : IClaimsTransformation
{
    /// <summary>Carries the user's stored <see cref="UserState"/> enum name.</summary>
    public const string UserStateClaimType = "UserState";

    public const string ActiveClaimValue = "true";
    public const string ClaimsAddedMarkerType = "RoleAssignmentClaimsAdded";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    /// <summary>The stored <see cref="UserState"/> stamped on the principal, or null if absent.</summary>
    public static UserState? GetUserState(ClaimsPrincipal principal) =>
        Enum.TryParse<UserState>(principal.FindFirstValue(UserStateClaimType), out var state)
            ? state
            : null;

    /// <summary>True when the principal's stored state grants full app access.</summary>
    public static bool IsActive(ClaimsPrincipal principal) =>
        GetUserState(principal) == UserState.Active;

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

        // Stored UserState is the single access source. GetUserInfoAsync seeds legacy null-State
        // rows on first read, so State is populated here.
        var userInfo = await userService.GetUserInfoAsync(userId);
        if (userInfo?.State is { } state)
        {
            claims.Add(new Claim(UserStateClaimType, state.ToString()));
        }

        var activeRoles = await roleAssignments.GetActiveForUserAsync(userId);
        foreach (var role in activeRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.RoleName));
        }

        return claims;
    }
}
