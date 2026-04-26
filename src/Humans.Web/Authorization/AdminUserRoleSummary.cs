using System.Security.Claims;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

/// <summary>
/// Picks a single label representing the user's most-privileged admin role,
/// for display in the admin sidebar's footer. Order is roughly broadest-scope
/// to narrowest.
/// </summary>
public static class AdminUserRoleSummary
{
    private static readonly string[] Order =
    {
        RoleNames.Admin, RoleNames.Board, RoleNames.HumanAdmin, RoleNames.FinanceAdmin,
        RoleNames.TicketAdmin, RoleNames.TeamsAdmin, RoleNames.CampAdmin,
        RoleNames.FeedbackAdmin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator,
        RoleNames.ConsentCoordinator,
    };

    public static string PrimaryRole(ClaimsPrincipal user)
    {
        foreach (var role in Order)
            if (user.IsInRole(role)) return role;
        return string.Empty;
    }
}
