using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps.Authorization;

/// <summary>
/// Succeeds when the user is a CampAdmin or Admin, OR is a coordinator /
/// management role-holder on any team or sub-team. Gates the read-only Barrios
/// compliance matrix (<see cref="PolicyNames.CampComplianceAccess"/>) so camp
/// admins and team coordinators can both see role staffing across barrios,
/// without widening the CampAdmin-only management surface in
/// <c>CampAdminController</c>. Moved from Shifts' Contracts/ folder with the
/// handler at nobodies-collective/Humans#1091 — this section is the policy's
/// only consumer, so nothing needs it public any more.
/// </summary>
internal sealed class CampComplianceAccessRequirement : IAuthorizationRequirement;
