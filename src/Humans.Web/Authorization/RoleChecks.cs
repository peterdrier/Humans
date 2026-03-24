using System.Security.Claims;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

public static class RoleChecks
{
    private static readonly string[] AdminAssignableRoles =
    [
        RoleNames.Admin,
        RoleNames.Board,
        RoleNames.TeamsAdmin,
        RoleNames.CampAdmin,
        RoleNames.TicketAdmin,
        RoleNames.NoInfoAdmin,
        RoleNames.FeedbackAdmin,
        RoleNames.ConsentCoordinator,
        RoleNames.VolunteerCoordinator
    ];

    private static readonly string[] BoardAssignableRoles =
    [
        RoleNames.Board,
        RoleNames.TeamsAdmin,
        RoleNames.CampAdmin,
        RoleNames.TicketAdmin,
        RoleNames.NoInfoAdmin,
        RoleNames.FeedbackAdmin,
        RoleNames.ConsentCoordinator,
        RoleNames.VolunteerCoordinator
    ];

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

    public static bool BypassesMembershipRequirement(ClaimsPrincipal user)
    {
        return IsTeamsAdminBoardOrAdmin(user) ||
               IsCampAdmin(user) ||
               user.IsInRole(RoleNames.TicketAdmin) ||
               user.IsInRole(RoleNames.NoInfoAdmin) ||
               user.IsInRole(RoleNames.ConsentCoordinator) ||
               user.IsInRole(RoleNames.VolunteerCoordinator);
    }

    public static IReadOnlyList<string> GetAssignableRoles(ClaimsPrincipal user)
    {
        return IsAdmin(user) ? AdminAssignableRoles : BoardAssignableRoles;
    }

    public static bool IsFeedbackAdmin(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.FeedbackAdmin);
    }

    public static bool CanManageRole(ClaimsPrincipal user, string roleName)
    {
        if (IsAdmin(user))
        {
            return true;
        }

        if (IsBoard(user))
        {
            return string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal) ||
                   string.Equals(roleName, RoleNames.TeamsAdmin, StringComparison.Ordinal) ||
                   string.Equals(roleName, RoleNames.ConsentCoordinator, StringComparison.Ordinal) ||
                   string.Equals(roleName, RoleNames.VolunteerCoordinator, StringComparison.Ordinal) ||
                   string.Equals(roleName, RoleNames.FeedbackAdmin, StringComparison.Ordinal);
        }

        return false;
    }
}
