using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Web.Models.Vol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Vol")]
public class VolController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly ILogger<VolController> _logger;
    private readonly IClock _clock;

    public VolController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        ITeamService teamService,
        IProfileService profileService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        ILogger<VolController> logger,
        IClock clock)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _teamService = teamService;
        _profileService = profileService;
        _availabilityService = availabilityService;
        _logger = logger;
        _clock = clock;
    }

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(MyShifts));

    [HttpGet("MyShifts")]
    public async Task<IActionResult> MyShifts()
    {
        try
        {
            var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
            if (currentUserNotFound != null) return currentUserNotFound;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es == null) return View("NoActiveEvent");

            var signups = await _signupService.GetByUserAsync(user.Id, es.Id);

            var model = new Models.Vol.MyShiftsViewModel
            {
                EventSettings = es,
                Shifts = signups.Select(signup => new Models.Vol.MyShiftsViewModel.MyShiftRow
                {
                    SignupId = signup.Id,
                    DutyTitle = string.IsNullOrWhiteSpace(signup.Shift.Description)
                        ? signup.Shift.Rota.Name
                        : $"{signup.Shift.Rota.Name} — {signup.Shift.Description}",
                    TeamName = signup.Shift.Rota.Team.Name,
                    AbsoluteStart = signup.Shift.GetAbsoluteStart(es),
                    AbsoluteEnd = signup.Shift.GetAbsoluteEnd(es),
                    Status = signup.Status
                }).OrderBy(s => s.AbsoluteStart).ToList()
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading My Shifts");
            throw;
        }
    }

    [HttpPost("Bail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bail(Guid signupId)
    {
        try
        {
            var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
            if (currentUserNotFound != null) return currentUserNotFound;

            var result = await _signupService.BailAsync(signupId, user.Id, null);

            if (!result.Success)
            {
                SetError(result.Error ?? "Shift bail failed.");
                return RedirectToAction(nameof(MyShifts));
            }

            SetSuccess("Successfully bailed from shift.");
            return RedirectToAction(nameof(MyShifts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bailing from shift {SignupId}", signupId);
            throw;
        }
    }

    [HttpGet("Shifts")]
    public async Task<IActionResult> Shifts(Guid? departmentId, string? date, bool showFull = false)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err != null) return err;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es == null) return View("NoActiveEvent");

            var isPrivileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                               (await _shiftMgmt.GetCoordinatorDepartmentIdsAsync(user.Id)).Count > 0;

            var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
            var hasSignups = userSignups.Count > 0;

            if (!es.IsShiftBrowsingOpen && !isPrivileged && !hasSignups)
                return View("BrowsingClosed");

            var userSignupShiftIds = userSignups
                .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .Select(s => s.ShiftId)
                .ToHashSet();
            var userSignupStatuses = userSignups
                .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .ToDictionary(s => s.ShiftId, s => s.Status);

            // Parse date filter
            LocalDate? filterDate = null;
            if (!string.IsNullOrEmpty(date) && LocalDatePattern.Iso.Parse(date) is { Success: true } parseResult)
                filterDate = parseResult.Value;

            var browseShifts = await _shiftMgmt.GetBrowseShiftsAsync(
                es.Id, departmentId: departmentId, date: filterDate,
                includeAdminOnly: isPrivileged, includeSignups: isPrivileged);

            // Group by department -> rota -> shift
            var departments = browseShifts
                .GroupBy(u => u.Shift.Rota.TeamId)
                .Select(deptGroup =>
                {
                    var firstShift = deptGroup.First().Shift;
                    return new DepartmentShiftGroup
                    {
                        TeamId = firstShift.Rota.TeamId,
                        TeamName = firstShift.Rota.Team.Name,
                        TeamSlug = firstShift.Rota.Team.Slug,
                        Rotas = deptGroup.GroupBy(u => u.Shift.RotaId)
                            .Select(rotaGroup =>
                            {
                                var rota = rotaGroup.First().Shift.Rota;
                                return new RotaShiftGroup
                                {
                                    Rota = rota,
                                    Shifts = rotaGroup.Select(u =>
                                    {
                                        var (start, end, period) = _shiftMgmt.ResolveShiftTimes(u.Shift, es);
                                        return new ShiftDisplayItem
                                        {
                                            Shift = u.Shift,
                                            AbsoluteStart = start,
                                            AbsoluteEnd = end,
                                            Period = period,
                                            ConfirmedCount = u.ConfirmedCount,
                                            RemainingSlots = u.RemainingSlots,
                                            Signups = u.Signups.Select(s => new ShiftSignupInfo(
                                                s.UserId, s.DisplayName, s.Status,
                                                s.HasProfilePicture ? $"/Human/{s.UserId}/Picture" : null))
                                                .ToList()
                                        };
                                    }).OrderBy(s => s.AbsoluteStart).ToList()
                                };
                            }).OrderBy(r => r.Rota.Name, StringComparer.Ordinal).ToList()
                    };
                }).OrderBy(d => d.TeamName, StringComparer.Ordinal).ToList();

            List<DepartmentOption> allDepartments;
            if (!departmentId.HasValue)
            {
                allDepartments = departments
                    .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName }).ToList();
            }
            else
            {
                var depts = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
                allDepartments = depts
                    .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName }).ToList();
            }

            var model = new ShiftBrowserViewModel
            {
                EventSettings = es,
                FilterDepartmentId = departmentId,
                FilterDate = date,
                ShowFullShifts = showFull,
                UserSignupShiftIds = userSignupShiftIds,
                UserSignupStatuses = userSignupStatuses,
                Departments = departments,
                AllDepartments = allDepartments,
                ShowSignups = isPrivileged,
                IsPrivileged = isPrivileged
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading All Shifts page");
            SetError("Failed to load shifts.");
            return RedirectToAction(nameof(MyShifts));
        }
    }

    [HttpPost("SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err != null) return err;

            var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
            var result = await _signupService.SignUpAsync(user.Id, shiftId, isPrivileged: privileged);

            if (!result.Success)
            {
                SetError(result.Error ?? "Shift signup failed.");
                return RedirectToAction(nameof(Shifts));
            }

            SetSuccess(result.Warning != null
                ? $"Signed up successfully. Note: {result.Warning}"
                : "Signed up successfully!");

            return RedirectToAction(nameof(Shifts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error signing up for shift {ShiftId}", shiftId);
            SetError("Failed to sign up.");
            return RedirectToAction(nameof(Shifts));
        }
    }
}
