using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
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

[Authorize(Roles = RoleNames.Admin + "," + RoleNames.NoInfoAdmin + "," + RoleNames.VolunteerCoordinator)]
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
    public async Task<IActionResult> Index(Guid? departmentId, Guid? rotaId, string? date)
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
            SelectedRotaId = rotaId,
            SelectedDate = date,
            EventSettings = es,
            StaffingData = staffingData.ToList()
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
