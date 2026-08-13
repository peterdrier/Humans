using System.Security.Claims;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.UI.Authorization;

public static class RoleChecks
{
    private static readonly IReadOnlyList<string> AdminAssignableRoles =
        [RoleNames.Admin, .. RoleNames.BoardManageableRoles];

    private static readonly IReadOnlyList<string> BoardAssignableRoles =
        [.. RoleNames.BoardManageableRoles];

    /// <summary>Carries the user's stored <see cref="UserState"/> enum name.</summary>
    /// <remarks>
    /// Owned here rather than on Shell's <c>RoleAssignmentClaimsTransformation</c> (which
    /// stamps it) because a section view reads it: Camps' index page renders the
    /// "register a camp" call to action only for an Active member. A section cannot name a
    /// <c>Humans.Web</c> type, and the predicate carries no section vocabulary — the same
    /// test as the rest of this class (design §15 step 6, the Shell-resident-helper rule).
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

    public static bool IsTeamsAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.TeamsAdmin);
    }

    public static bool IsTeamsAdminBoardOrAdmin(ClaimsPrincipal user)
    {
        return IsAdminOrBoard(user) || IsTeamsAdmin(user);
    }

    public static bool IsCampAdmin(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.CampAdmin);
    }

    public static bool IsEventsAdmin(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.EventsAdmin);
    }

    public static bool CanAccessReviewQueue(ClaimsPrincipal user)
    {
        return IsAdminOrBoard(user) ||
               user.IsInRole(RoleNames.ConsentCoordinator) ||
               user.IsInRole(RoleNames.VolunteerCoordinator);
    }

    public static bool CanAccessTickets(ClaimsPrincipal user)
    {
        return IsAdminOrBoard(user) || user.IsInRole(RoleNames.TicketAdmin);
    }

    public static bool CanManageTickets(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.TicketAdmin);
    }

    /// <summary>
    /// Roles the current principal may assign from the human admin surface.
    /// </summary>
    public static IReadOnlyList<string> GetAssignableRoles(ClaimsPrincipal user)
    {
        if (IsAdmin(user))
            return AdminAssignableRoles;
        if (IsBoard(user) || IsHumanAdmin(user))
            return BoardAssignableRoles;
        return [];
    }

    public static bool IsHumanAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.HumanAdmin);
    }

    public static bool IsHumanAdminBoardOrAdmin(ClaimsPrincipal user)
    {
        return IsAdminOrBoard(user) || IsHumanAdmin(user);
    }

    public static bool IsFinanceAdmin(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.FinanceAdmin);
    }

    public static bool CanAccessFinance(ClaimsPrincipal user)
    {
        return IsFinanceAdmin(user);
    }

    /// <summary>
    /// Store domain superset (per <c>memory/code/admin-role-superset.md</c>):
    /// <c>StoreAdmin</c> owns the Store section end-to-end (catalog, orders, payments,
    /// invoices). <c>FinanceAdmin</c> retains parallel access for accounting workflows.
    /// </summary>
    public static bool CanAdministerStore(ClaimsPrincipal user)
    {
        return IsAdmin(user)
            || user.IsInRole(RoleNames.StoreAdmin)
            || user.IsInRole(RoleNames.FinanceAdmin);
    }

    /// <summary>
    /// Admin or VolunteerCoordinator — intentionally excludes NoInfoAdmin,
    /// who can approve shift signups but not manage rotas/departments.
    /// </summary>
    public static bool IsVolunteerManager(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.VolunteerCoordinator);
    }

    public static bool CanManageRole(ClaimsPrincipal user, string roleName)
    {
        if (IsAdmin(user))
        {
            return true;
        }

        if (IsBoard(user) || IsHumanAdmin(user))
        {
            return RoleNames.BoardManageableRoles.Contains(roleName);
        }

        return false;
    }
}
