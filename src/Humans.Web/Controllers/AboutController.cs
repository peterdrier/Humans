using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Helpers;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Route("[controller]")]
public class AboutController : HumansControllerBase
{
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IProfileService _profileService;
    private readonly IClock _clock;
    private readonly ILogger<AboutController> _logger;

    public AboutController(
        UserManager<User> userManager,
        IRoleAssignmentService roleAssignmentService,
        IProfileService profileService,
        IClock clock,
        ILogger<AboutController> logger)
        : base(userManager)
    {
        _roleAssignmentService = roleAssignmentService;
        _profileService = profileService;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [Authorize]
    [HttpGet("Staff")]
    public async Task<IActionResult> Staff()
    {
        try
        {
            var now = _clock.GetCurrentInstant();

            // Load all active role assignments with user data — ~500 users, fits in memory
            var (assignments, _) = await _roleAssignmentService.GetFilteredAsync(
                roleFilter: null, activeOnly: true, page: 1, pageSize: 500, now);

            // Resolve effective profile picture URLs (custom uploads take priority over Google avatars)
            var effectiveUrls = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
                _profileService, Url,
                assignments.Select(ra => (ra.UserId, ra.User.ProfilePictureUrl)));

            // Define role display order and metadata
            var roleDefinitions = StaffViewModel.GetRoleDefinitions();

            var roleSections = new List<StaffRoleSectionViewModel>();

            foreach (var roleDef in roleDefinitions)
            {
                var holders = assignments
                    .Where(ra => string.Equals(ra.RoleName, roleDef.RoleName, StringComparison.Ordinal))
                    .Select(ra => new StaffRoleHolderViewModel
                    {
                        UserId = ra.UserId,
                        DisplayName = ra.User.DisplayName,
                        ProfilePictureUrl = effectiveUrls.GetValueOrDefault(ra.UserId)
                    })
                    .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (holders.Count > 0)
                {
                    roleSections.Add(new StaffRoleSectionViewModel
                    {
                        RoleName = roleDef.RoleName,
                        DisplayTitle = roleDef.DisplayTitle,
                        Blurb = roleDef.Blurb,
                        Icon = roleDef.Icon,
                        Holders = holders
                    });
                }
            }

            var viewModel = new StaffViewModel
            {
                RoleSections = roleSections
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load staff page");
            return View(new StaffViewModel { RoleSections = [] });
        }
    }
}
