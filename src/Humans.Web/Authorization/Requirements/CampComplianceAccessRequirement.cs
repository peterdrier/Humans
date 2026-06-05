using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user is a CampAdmin or Admin, OR is a coordinator /
/// management role-holder on any team or sub-team. Gates the read-only Barrios
/// compliance matrix (<see cref="PolicyNames.CampComplianceAccess"/>) so camp
/// admins and team coordinators can both see role staffing across barrios,
/// without widening the CampAdmin-only management surface in
/// <c>CampAdminController</c>.
/// </summary>
public class CampComplianceAccessRequirement : IAuthorizationRequirement;
