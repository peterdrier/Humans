using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Camps.Controllers;

/// <summary>
/// Split from <see cref="CampAdminController"/> so the broader <see cref="CampsPolicies.Compliance"/> can
/// gate it while camp management stays CampAdmin-only.
/// </summary>
[Authorize(Policy = CampsPolicies.Compliance)]
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

        var columns = matrix.Roles.Select((role, index) => (Role: role, Index: index))
            .OrderBy(column => column.Role.SortOrder)
            .ThenBy(column => column.Role.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(new CampComplianceViewModel
        {
            Event = matrix.Event,
            EventOptions = settings.Events,
            Roles = columns
                .Select(column => new CampComplianceRoleColumn(column.Role.Id, column.Role.Name, column.Role.MinimumRequired))
                .ToList(),
            Rows = matrix.Rows
                .OrderBy(r => r.CampName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new CampComplianceRow(
                    r.CampName,
                    r.CampSlug,
                    r.JoinedMemberCount,
                    r.TargetMemberCount,
                    columns
                        .Select(column => new CampComplianceRoleCell(
                            r.AssigneeUserIdsByRole[column.Index], column.Role.MinimumRequired))
                        .ToList()))
                .ToList(),
        });
    }
}
