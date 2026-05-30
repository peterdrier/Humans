using Humans.Application.Enums;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

// Wider policy for page entry; privileged sub-panels gated by ShiftDashboardAccess in views.
[Authorize(Policy = PolicyNames.ShiftDepartmentManager)]
[Route("Shifts/Dashboard")]
public class ShiftDashboardController(
    IShiftManagementService shiftMgmt,
    IShiftSignupService signupService,
    IUserServiceRead userService,
    ShiftDashboardPageBuilder pageBuilder,
    ShiftVolunteerSearchBuilder volunteerSearchBuilder,
    ILogger<ShiftDashboardController> logger) : HumansControllerBase(userService)
{
    private static LocalDate? ParseIsoDateOrNull(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var parsed = LocalDatePattern.Iso.Parse(raw);
        return parsed.Success ? parsed.Value : null;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        Guid? departmentId,
        Guid? rotaId,
        string? startDate,
        string? endDate,
        TrendWindow? trendWindow,
        ShiftPeriod? period,
        BuildSubPeriod? subPeriod)
    {
        var es = await shiftMgmt.GetActiveAsync();
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        var filterStartDate = ParseIsoDateOrNull(startDate);
        var filterEndDate = ParseIsoDateOrNull(endDate);
        var (activeStart, activeEnd) = ShiftFilterResolver.Resolve(period, filterStartDate, filterEndDate);

        var model = await pageBuilder.BuildAsync(new ShiftDashboardPageRequest(
            es,
            departmentId,
            rotaId,
            startDate,
            endDate,
            filterStartDate,
            filterEndDate,
            activeStart,
            activeEnd,
            trendWindow ?? TrendWindow.Last30Days,
            period,
            subPeriod));

        return View(model);
    }

    // Auth: narrow policy overrides controller-level wider one (subteam managers can't reach directly).
    [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query)
    {
        try
        {
            var result = await volunteerSearchBuilder.BuildForShiftAsync(
                await shiftMgmt.GetShiftByIdAsync(shiftId),
                query,
                ShiftRoleChecks.CanViewMedical(User));
            return ToVolunteerSearchActionResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

    private IActionResult ToVolunteerSearchActionResult(VolunteerSearchBuildResult result) =>
        result.Status switch
        {
            VolunteerSearchBuildStatus.EmptyQuery => Json(Array.Empty<VolunteerSearchResult>()),
            VolunteerSearchBuildStatus.NotFound => NotFound(),
            VolunteerSearchBuildStatus.Success => Json(result.Results),
            _ => throw new InvalidOperationException($"Unexpected volunteer search status '{result.Status}'.")
        };

    // Auth: narrow policy overrides controller-level wider one — only ShiftDashboardAccess can assign.
    [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
    [HttpPost("Voluntell")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Voluntell(Guid shiftId, Guid userId)
    {
        var (currentUserNotFound, currentUser) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var result = await signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
        if (result.Success)
        {
            SetSuccess("Volunteer assigned to shift.");
        }
        else
        {
            SetError(result.Error ?? "Failed to assign volunteer.");
        }

        return RedirectToAction(nameof(Index));
    }
}
