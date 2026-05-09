using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

// RoleAssignment cross-domain nav properties (User, CreatedByUser) are [Obsolete] —
// RoleAssignmentService stitches them in memory from IUserService so controllers can
// continue to read them for view-model shaping. Nav-strip follow-up tracked in
// design-rules §15i.
#pragma warning disable CS0618

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
            var viewModel = await BuildStaffViewModelAsync();
            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load staff page");
            return View(new StaffViewModel { RoleSections = [] });
        }
    }

    private async Task<StaffViewModel> BuildStaffViewModelAsync()
    {
        var now = _clock.GetCurrentInstant();

        // Load all active role assignments with user data — ~500 users, fits in memory
        var (assignments, _) = await _roleAssignmentService.GetFilteredAsync(
            roleFilter: null, activeOnly: true, page: 1, pageSize: 500, now);

        // Resolve effective profile picture URLs (custom uploads only — see issue #532).
        var effectiveUrls = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            _profileService, Url,
            assignments.Select(ra => ra.UserId));

        // Issue #692: BurnerName-aware names — batch-fetch profiles for
        // every role-holder so the public Staff page uses BurnerName.
        var assignmentUserIds = assignments.Select(ra => ra.UserId).Distinct().ToList();
        var assignmentProfiles = assignmentUserIds.Count > 0
            ? await _profileService.GetByUserIdsAsync(assignmentUserIds)
            : (IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile>)new Dictionary<Guid, Humans.Domain.Entities.Profile>();

        var roleSections = StaffViewModel.GetRoleDefinitions()
            .Select(def => BuildRoleSection(def, assignments, assignmentProfiles, effectiveUrls))
            .Where(s => s.Holders.Count > 0)
            .ToList();

        return new StaffViewModel { RoleSections = roleSections };
    }

    private static StaffRoleSectionViewModel BuildRoleSection(
        StaffRoleDefinition roleDef,
        IEnumerable<Humans.Domain.Entities.RoleAssignment> assignments,
        IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile> profiles,
        IReadOnlyDictionary<Guid, string?> effectiveUrls)
    {
        var holders = assignments
            .Where(ra => string.Equals(ra.RoleName, roleDef.RoleName, StringComparison.Ordinal))
            .Select(ra => new StaffRoleHolderViewModel
            {
                UserId = ra.UserId,
                DisplayName = ResolveStaffName(ra, profiles),
                ProfilePictureUrl = effectiveUrls.GetValueOrDefault(ra.UserId)
            })
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StaffRoleSectionViewModel
        {
            RoleName = roleDef.RoleName,
            DisplayTitle = roleDef.DisplayTitle,
            Blurb = roleDef.Blurb,
            Icon = roleDef.Icon,
            Holders = holders
        };
    }

    /// <summary>
    /// Issue #692: BurnerName-aware staff label.
    /// </summary>
    private static string ResolveStaffName(
        Humans.Domain.Entities.RoleAssignment ra,
        IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile> profiles)
    {
        if (profiles.TryGetValue(ra.UserId, out var p) && !string.IsNullOrWhiteSpace(p.BurnerName))
            return p.BurnerName;
        return ra.User.DisplayName;
    }
}
