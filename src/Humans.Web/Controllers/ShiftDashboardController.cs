using Humans.Application.Enums;
using Humans.Application.Interfaces;
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

[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
[Route("Shifts/Dashboard")]
public class ShiftDashboardController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly UserManager<User> _userManager;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ShiftDashboardController> _logger;

    public ShiftDashboardController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        IWebHostEnvironment environment,
        ILogger<ShiftDashboardController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _userManager = userManager;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        Guid? departmentId,
        Guid? rotaId,
        string? date,
        TrendWindow? trendWindow,
        ShiftPeriod? period)
    {
        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        LocalDate? filterDate = null;
        if (!string.IsNullOrEmpty(date))
        {
            var parseResult = LocalDatePattern.Iso.Parse(date);
            if (parseResult.Success)
                filterDate = parseResult.Value;
        }

        var window = trendWindow ?? TrendWindow.Last30Days;

        // Sequential awaits — shared scoped DbContext is not safe for concurrent queries.
        var shifts = await _shiftMgmt.GetUrgentShiftsAsync(es.Id, limit: null, departmentId, filterDate, period);
        var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id, departmentId, period);
        var staffingHours = await _shiftMgmt.GetStaffingHoursAsync(es.Id, departmentId, period);
        var overview = await _shiftMgmt.GetDashboardOverviewAsync(es.Id, period);
        var coordinatorActivity = await _shiftMgmt.GetCoordinatorActivityAsync(es.Id, period);
        // Always fetch the full history; the partial slices client-side on window toggle
        // so the user doesn't incur a full page reload to change the trend range.
        var trends = await _shiftMgmt.GetDashboardTrendsAsync(es.Id, TrendWindow.All, period);
        var deptTuples = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);

        var departments = deptTuples.Select(d => new DepartmentOption
        {
            TeamId = d.TeamId,
            Name = d.TeamName
        }).ToList();

        var model = new ShiftDashboardViewModel
        {
            Shifts = shifts.ToList(),
            Departments = departments,
            SelectedDepartmentId = departmentId,
            SelectedRotaId = rotaId,
            SelectedDate = date,
            SelectedPeriod = period,
            EventSettings = es,
            StaffingData = staffingData.ToList(),
            StaffingHours = staffingHours.ToList(),
            Overview = overview,
            CoordinatorActivity = coordinatorActivity,
            Trends = trends,
            TrendWindow = window,
            IsDevelopment = _environment.IsDevelopment(),
        };

        return View(model);
    }

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
