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
    public async Task<IActionResult> Compliance(Guid? eventSettingsId, CancellationToken ct)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var resolvedEventId = eventSettingsId
            ?? settings.ActiveEventSettingsId
            ?? settings.Events.FirstOrDefault()?.EventSettingsId;
        var matrix = resolvedEventId is { } id
            ? await campRoleService.BuildComplianceMatrixAsync(id, ct)
            : null;
        if (matrix is null) return NotFound();

        return View(new CampComplianceViewModel
        {
            Event = matrix.Event,
            EventOptions = settings.Events,
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
