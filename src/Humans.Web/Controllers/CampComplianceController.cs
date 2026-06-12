using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models.CampAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Read-only Barrios role-compliance matrix at <c>/Barrios/Admin/Compliance</c>.
/// Split out from <see cref="CampAdminController"/> so it can be gated by the
/// broader <see cref="PolicyNames.CampComplianceAccess"/> (CampAdmin/Admin OR any
/// team coordinator) while the camp-management actions stay CampAdmin-only.
/// </summary>
[Authorize(Policy = PolicyNames.CampComplianceAccess)]
[Route("Barrios/Admin")]
[Route("Camps/Admin")]
public class CampComplianceController(
    ICampServiceRead campService,
    ICampRoleService campRoleService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("Compliance")]
    public async Task<IActionResult> Compliance(int? year, CancellationToken ct)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var matrix = await campRoleService.BuildComplianceMatrixAsync(year ?? settings.PublicYear, ct);

        return View(new CampComplianceViewModel
        {
            Year = matrix.Year,
            Roles = matrix.Roles
                .Select(r => new CampComplianceRoleColumn(r.Id, r.Name, r.MinimumRequired))
                .ToList(),
            Rows = matrix.Rows
                .OrderBy(r => r.CampName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new CampComplianceRow(
                    r.CampName,
                    r.CampSlug,
                    r.JoinedMemberCount,
                    r.TargetMemberCount,
                    r.AssigneeUserIdsByRole
                        .Select((assignees, i) => new CampComplianceRoleCell(
                            assignees, matrix.Roles[i].MinimumRequired))
                        .ToList()))
                .ToList(),
        });
    }
}
