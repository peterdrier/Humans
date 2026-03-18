using System.Security.Claims;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

public static class RoleChecks
{
    public static bool IsAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.Admin);
    }

    public static bool IsAdminOrBoard(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.Board);
    }

    public static bool IsTeamsAdminBoardOrAdmin(ClaimsPrincipal user)
    {
        return IsAdminOrBoard(user) || user.IsInRole(RoleNames.TeamsAdmin);
    }

    public static bool IsCampAdmin(ClaimsPrincipal user)
    {
        return IsAdmin(user) || user.IsInRole(RoleNames.CampAdmin);
    }
}
