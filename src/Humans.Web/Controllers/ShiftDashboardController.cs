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
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Shifts/Dashboard")]
public class ShiftDashboardController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly IProfileService _profileService;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<ShiftDashboardController> _logger;

    public ShiftDashboardController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        IProfileService profileService,
        UserManager<User> userManager,
        ILogger<ShiftDashboardController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _profileService = profileService;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? departmentId, string? date)
    {
        if (!ShiftRoleChecks.CanAccessDashboard(User))
            return Forbid();

        var es = await _shiftMgmt.GetActiveAsync();
        if (es == null)
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

        var shifts = await _shiftMgmt.GetUrgentShiftsAsync(es.Id, limit: null, departmentId, filterDate);
        var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id, departmentId);

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
            SelectedDate = date,
            EventSettings = es,
            StaffingData = staffingData.ToList()
        };

        return View(model);
    }

    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query)
    {
        if (!ShiftRoleChecks.CanAccessDashboard(User))
            return Forbid();

        if (!query.HasSearchTerm())
            return Json(Array.Empty<VolunteerSearchResult>());

        try
        {
            var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
            if (shift == null) return NotFound();

            var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
            if (es == null) return NotFound();

            var results = await ShiftVolunteerSearchBuilder.BuildAsync(
                shift,
                query,
                es,
                ShiftRoleChecks.CanViewMedical(User),
                _userManager,
                _profileService,
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
        if (!ShiftRoleChecks.CanAccessDashboard(User))
            return Forbid();

        var (currentUserNotFound, currentUser) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserNotFound != null)
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
