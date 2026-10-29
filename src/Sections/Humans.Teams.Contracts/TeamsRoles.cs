using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Teams.Contracts;

/// <summary>Teams' grantable role and its predicates. Declared to Auth by Teams' <c>Section</c>.</summary>
public static class TeamsRoles
{
    /// <summary>
    /// Teams Admin: manages every team, approves membership, assigns leads and configures Google Group prefixes
    /// system-wide.
    /// </summary>
    public const string TeamsAdmin = "TeamsAdmin";

    public static bool IsTeamsAdmin(ClaimsPrincipal user) => user.IsInRole(TeamsAdmin);

    public static bool IsTeamsAdminBoardOrAdmin(ClaimsPrincipal user) =>
        RoleChecks.IsAdminOrBoard(user) || IsTeamsAdmin(user);
}
