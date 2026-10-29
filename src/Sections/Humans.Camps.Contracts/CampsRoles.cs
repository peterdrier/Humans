using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Camps.Contracts;

/// <summary>Camps' grantable role and its predicate. Declared to Auth by Camps' <c>Section</c>.</summary>
public static class CampsRoles
{
    /// <summary>
    /// Camps Admin: manages camps, approves/rejects season registrations and configures camp settings system-wide.
    /// Stored as <c>CampAdmin</c> before 2026-10.
    /// </summary>
    public const string CampsAdmin = "CampsAdmin";

    /// <summary>Admin or Camps Admin.</summary>
    public static bool IsCampsAdmin(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(CampsAdmin);
}
