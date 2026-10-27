using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Users.Contracts;

namespace Humans.Base.Authorization;

/// <summary>
/// Predicates over the two system-wide roles and the stored user state. A section's own roles come with their own
/// predicates on its <c>&lt;Section&gt;Roles</c> class.
/// </summary>
public static class RoleChecks
{
    /// <summary>Carries the user's stored <see cref="UserState"/> enum name.</summary>
    /// <remarks>
    /// Here, not on Shell's <c>RoleAssignmentClaimsTransformation</c> which stamps it, because a section view (Camps'
    /// index) reads it and a section cannot name a <c>Humans.Web</c> type.
    /// </remarks>
    public const string UserStateClaimType = "UserState";

    /// <summary>The stored <see cref="UserState"/> stamped on the principal, or null if absent.</summary>
    public static UserState? GetUserState(ClaimsPrincipal user) =>
        Enum.TryParse<UserState>(user.FindFirstValue(UserStateClaimType), out var state)
            ? state
            : null;

    /// <summary>True when the principal's stored state grants full app access.</summary>
    public static bool IsActiveMember(ClaimsPrincipal user) =>
        GetUserState(user) == UserState.Active;

    public static bool IsAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.Admin);
    }

    public static bool IsBoard(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.Board);
    }

    public static bool IsAdminOrBoard(ClaimsPrincipal user)
    {
        return IsAdmin(user) || IsBoard(user);
    }
}
