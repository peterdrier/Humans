using Humans.Application.Enums;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Helpers;
using Humans.Web.Models;
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
    private readonly IWebHostEnvironment _environment;
    private readonly IClock _clock;
    private readonly ILogger<ShiftDashboardController> _logger;

    public ShiftDashboardController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        IWebHostEnvironment environment,
        IClock clock,
        ILogger<ShiftDashboardController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _userManager = userManager;
        _environment = environment;
        _clock = clock;
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

        LocalDate? filterStartDate = ParseIsoDateOrNull(startDate);
        LocalDate? filterEndDate = ParseIsoDateOrNull(endDate);
        var (activeStart, activeEnd) = ResolveActiveDateRange(period, filterStartDate, filterEndDate);

        var window = trendWindow ?? TrendWindow.Last30Days;

        // Sequential awaits — shared scoped DbContext is not safe for concurrent queries.
        var shifts = await _shiftMgmt.GetUrgentShiftsAsync(es.Id, limit: null, departmentId, activeStart, activeEnd, period, subPeriod);
        var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id, departmentId, period, subPeriod);
        var staffingHours = await _shiftMgmt.GetStaffingHoursAsync(es.Id, departmentId, period, subPeriod);
        var overview = await _shiftMgmt.GetDashboardOverviewAsync(es.Id, period, subPeriod);
        var coordinatorActivity = await _shiftMgmt.GetCoordinatorActivityAsync(es.Id, period, subPeriod);
        // Always fetch the full history; the partial slices client-side on window toggle
        // so the user doesn't incur a full page reload to change the trend range.
        var trends = await _shiftMgmt.GetDashboardTrendsAsync(es.Id, TrendWindow.All, period, subPeriod);
        var dailyDeptStaffing = await _shiftMgmt.GetDailyDepartmentStaffingAsync(es.Id, period, subPeriod);
        var shiftDurationBreakdown = await _shiftMgmt.GetShiftDurationBreakdownAsync(es.Id, period, subPeriod);
        var coverageHeatmap = await _shiftMgmt.GetCoverageHeatmapAsync(es.Id, period, subPeriod);
        var deptTuples = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);

        var departments = deptTuples.Select(d => new DepartmentOption
        {
            TeamId = d.TeamId,
            Name = d.TeamName
        }).ToList();

        // Countdown to "feet on the ground" (first build day). Computed in the event
        // timezone so midnight-local is the reference, and from the injected IClock
        // so tests can override with FakeClock.
        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;
        var todayLocal = _clock.GetCurrentInstant().InZone(tz).Date;
        var firstBuildDay = es.GateOpeningDate.PlusDays(es.BuildStartOffset);
        var daysToBuild = Period.Between(todayLocal, firstBuildDay, PeriodUnits.Days).Days;
        var countdown = new BuildDayCountdown(
            DaysToBuild: daysToBuild,
            FirstBuildDay: firstBuildDay,
            Weeks: Math.Abs(daysToBuild) / 7,
            RemainderDays: Math.Abs(daysToBuild) % 7);

        var model = new ShiftDashboardViewModel
        {
            Shifts = shifts.ToList(),
            Departments = departments,
            SelectedDepartmentId = departmentId,
            SelectedRotaId = rotaId,
            SelectedStartDate = startDate,
            SelectedEndDate = endDate,
            SelectedPeriod = period,
            SelectedSubPeriod = subPeriod,
            FilterStartDate = filterStartDate,
            FilterEndDate = filterEndDate,
            EventSettings = es,
            StaffingData = staffingData.ToList(),
            StaffingHours = staffingHours.ToList(),
            Overview = overview,
            CoordinatorActivity = coordinatorActivity,
            Trends = trends,
            DailyDepartmentStaffing = dailyDeptStaffing,
            ShiftDurationBreakdown = shiftDurationBreakdown,
            CoverageHeatmap = coverageHeatmap,
            TrendWindow = window,
            IsDevelopment = _environment.IsDevelopment(),
            Countdown = countdown,
        };

        return View(model);
    }

    // Privileged action — overrides the controller-level wider policy with the
    // narrow ShiftDashboardAccess so subteam managers can't volunteer-search by
    // hitting the endpoint directly.
    [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query)
    {
        if (!query.HasSearchTerm())
            return Json(Array.Empty<VolunteerSearchResult>());

        try
        {
            var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
            if (shift is null) return NotFound();

            var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
            if (es is null) return NotFound();

            var results = await ShiftVolunteerSearchBuilder.BuildAsync(
                shift,
                query,
                es,
                ShiftRoleChecks.CanViewMedical(User),
                _userManager,
                _shiftMgmt,
                _signupService,
                _availabilityService);
            return Json(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

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
