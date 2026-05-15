using Humans.Application.Enums;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

// Page entry uses the WIDER policy so any team coordinator / sub-team manager
// can see the dashboard. Privileged sub-panels (coordinator activity, pending
// shifts, voluntell action) stay gated by the NARROWER ShiftDashboardAccess
// policy in the view itself.
[Authorize(Policy = PolicyNames.ShiftDepartmentManager)]
[Route("Shifts/Dashboard")]
public class ShiftDashboardController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly UserManager<User> _userManager;
    private readonly ShiftDashboardPageBuilder _pageBuilder;
    private readonly ILogger<ShiftDashboardController> _logger;

    public ShiftDashboardController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        ShiftDashboardPageBuilder pageBuilder,
        ILogger<ShiftDashboardController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _userManager = userManager;
        _pageBuilder = pageBuilder;
        _logger = logger;
    }

    private static LocalDate? ParseIsoDateOrNull(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var parsed = LocalDatePattern.Iso.Parse(raw);
        return parsed.Success ? parsed.Value : null;
    }

    // When a period/sub-period is selected, JS auto-populates the date inputs
    // with that range as a visual cue — dates are display-only in that case.
    // Only when period is null AND a date is present do dates take over as the
    // filter on the urgent-shifts list. Internal for unit-test access; this is
    // the server's enforcement of the period↔date-range mutex
    // (Shifts.md invariant line 237).
    internal static (LocalDate? activeStart, LocalDate? activeEnd) ResolveActiveDateRange(
        ShiftPeriod? period, LocalDate? filterStartDate, LocalDate? filterEndDate)
    {
        var datesAreFilter = !period.HasValue && (filterStartDate.HasValue || filterEndDate.HasValue);
        return (
            datesAreFilter ? filterStartDate : null,
            datesAreFilter ? filterEndDate : null);
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
        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        var filterStartDate = ParseIsoDateOrNull(startDate);
        var filterEndDate = ParseIsoDateOrNull(endDate);
        var (activeStart, activeEnd) = ResolveActiveDateRange(period, filterStartDate, filterEndDate);

        var model = await _pageBuilder.BuildAsync(new ShiftDashboardPageRequest(
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

    // Privileged action — overrides the controller-level wider policy with the
    // narrow ShiftDashboardAccess so subteam managers can't volunteer-search by
    // hitting the endpoint directly.
    [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query)
    {
        try
        {
            var result = await ShiftVolunteerSearchBuilder.BuildForShiftAsync(
                await _shiftMgmt.GetShiftByIdAsync(shiftId),
                query,
                _shiftMgmt.GetActiveAsync,
                ShiftRoleChecks.CanViewMedical(User),
                _userManager,
                _shiftMgmt,
                _signupService,
                _availabilityService);
            return ToVolunteerSearchActionResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
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

    // Privileged action — only the narrow ShiftDashboardAccess role list can
    // assign humans to shifts. Hides from the wider ShiftDepartmentManager
    // policy that gates the page entry.
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

        var result = await _signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
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
