using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Web.Models;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Users;

// Obsolete RoleAssignment nav props (User, CreatedByUser) stitched in-memory by RoleAssignmentService; see design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[Route("[controller]")]
public class AboutController(
    IUserServiceRead userService,
    IRoleAssignmentService roleAssignmentService,
    IClock clock,
    ILogger<AboutController> logger) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

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
            var now = clock.GetCurrentInstant();

            // Load all active role assignments with user data — ~500 users, fits in memory
            var (assignments, _) = await roleAssignmentService.GetFilteredAsync(
                roleFilter: null, activeOnly: true, page: 1, pageSize: 500, now);

            var assigneeInfos = await _userService.GetUserInfosAsync(
                assignments.Select(ra => ra.UserId).Distinct().ToList());

            var roleDefinitions = StaffViewModel.GetRoleDefinitions();

            var roleSections = new List<StaffRoleSectionViewModel>();

            foreach (var roleDef in roleDefinitions)
            {
                var holders = assignments
                    .Where(ra => string.Equals(ra.RoleName, roleDef.RoleName, StringComparison.Ordinal))
                    .Select(ra => new
                    {
                        Holder = new StaffRoleHolderViewModel { UserId = ra.UserId },
                        SortKey = assigneeInfos.GetValueOrDefault(ra.UserId)?.BurnerName ?? string.Empty
                    })
                    .OrderBy(h => h.SortKey, StringComparer.OrdinalIgnoreCase)
                    .Select(h => h.Holder)
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
            logger.LogError(ex, "Failed to load staff page");
            return View(new StaffViewModel { RoleSections = [] });
        }
    }
}
