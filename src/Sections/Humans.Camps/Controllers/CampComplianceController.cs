using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Camps.Controllers;

/// <summary>
/// Split from <see cref="CampAdminController"/> so the broader <see cref="PolicyNames.CampComplianceAccess"/> can
/// gate it while camp management stays CampAdmin-only.
/// </summary>
[Authorize(Policy = PolicyNames.CampComplianceAccess)]
[Route("Barrios/Admin")]
[Route("Camps/Admin")]
internal sealed class CampComplianceController(
    ICampService campService,
    ICampRoleService campRoleService,
    IUserService userService) : HumansControllerBase(userService)
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
