using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models.Vol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

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

            var model = new MyShiftsViewModel
            {
                EventSettings = es,
                Shifts = signups.Select(signup => new MyShiftsViewModel.MyShiftRow
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
}
