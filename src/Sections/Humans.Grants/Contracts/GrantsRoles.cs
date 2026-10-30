using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Grants.Contracts;

/// <summary>Grants' grantable role and its predicates. Declared to Auth by Grants' <c>Section</c>.</summary>
public static class GrantsRoles
{
    /// <summary>
    /// Grants Admin: creates grant programs, names their managers, and issues, revokes and resends on any program.
    /// Confers nothing outside Grants.
    /// </summary>
    public const string GrantsAdmin = "GrantsAdmin";

    /// <summary>Grants Admin or Admin: changes every program.</summary>
    public static bool AdministersGrants(ClaimsPrincipal user) => user.IsInRole(GrantsAdmin) || RoleChecks.IsAdmin(user);

    /// <summary>Grants Admin, Board or Admin: sees every program.</summary>
    public static bool ReadsGrants(ClaimsPrincipal user) => AdministersGrants(user) || RoleChecks.IsBoard(user);
}
