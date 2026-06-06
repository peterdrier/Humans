using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
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
    private static readonly HashSet<CampSeasonStatus> ActiveStatuses =
        [CampSeasonStatus.Active, CampSeasonStatus.Full];

    [HttpGet("Compliance")]
    public async Task<IActionResult> Compliance(int? year, CancellationToken ct)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var resolvedYear = year ?? settings.PublicYear;

        var roleColumns = (await campRoleService.ListDefinitionsAsync(includeDeactivated: false, ct))
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new CampComplianceRoleColumn(d.Id, d.Name, d.MinimumRequired))
            .ToList();

        var camps = await campService.GetCampsForYearAsync(resolvedYear, ct);

        var rows = camps
            .Select(c => c.GetSeasonForYear(resolvedYear))
            .Where(s => s is not null && ActiveStatuses.Contains(s.Status))
            .Select(s => BuildRow(s!, roleColumns))
            .OrderBy(r => r.CampName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(new CampComplianceViewModel
        {
            Year = resolvedYear,
            Roles = roleColumns,
            Rows = rows,
        });
    }

    private static CampComplianceRow BuildRow(
        CampSeasonInfo season, IReadOnlyList<CampComplianceRoleColumn> roleColumns)
    {
        var activeMembers = season.ActiveMembers;

        var cells = roleColumns.Select(col =>
        {
            var assignees = activeMembers
                .Where(m => m.Roles.Contains(col.Name, StringComparer.Ordinal))
                .Select(m => m.UserId)
                .ToList();
            return new CampComplianceRoleCell(assignees, col.MinimumRequired);
        }).ToList();

        return new CampComplianceRow(
            season.Name,
            season.CampSlug,
            season.JoinedMemberCount ?? 0,
            season.MemberCount,
            cells);
    }
}
