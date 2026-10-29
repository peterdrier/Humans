using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Users.Authorization;

internal static class UsersRoleChecks
{
    public static bool IsUsersAdmin(ClaimsPrincipal user) => user.IsInRole(UsersRoles.UsersAdmin);

    public static bool IsUsersAdminBoardOrAdmin(ClaimsPrincipal user) =>
        RoleChecks.IsAdminOrBoard(user) || IsUsersAdmin(user);
}
