using Humans.Application.Interfaces;
using Humans.Domain.Entities;
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
    public IActionResult MyShifts() => View();
}
