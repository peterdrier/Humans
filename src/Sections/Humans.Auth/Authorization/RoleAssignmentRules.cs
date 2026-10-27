using System.Security.Claims;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Auth.Authorization;

/// <summary>
/// Who may assign and end which role: Admin any role, UsersAdmin every role but Admin. Board is a governance body,
/// not an operator, so it no longer assigns roles (Peter, 2026-10-22).
/// </summary>
internal static class RoleAssignmentRules
{
    public static bool MayManage(ClaimsPrincipal user, string roleName)
    {
        if (user.IsInRole(RoleNames.Admin))
            return true;

        return user.IsInRole(UsersRoles.UsersAdmin)
            && !string.Equals(roleName, RoleNames.Admin, StringComparison.Ordinal);
    }

    /// <summary>The roles the principal may assign, in catalog order.</summary>
    public static IReadOnlyList<RoleDefinition> Assignable(ClaimsPrincipal user, RoleCatalog catalog) =>
        [.. catalog.Roles.Where(r => MayManage(user, r.Name))];
}
