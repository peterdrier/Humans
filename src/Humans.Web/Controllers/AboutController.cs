using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Web.Models;
using Humans.Users.Contracts;

namespace Humans.Web.Controllers;

[Route("[controller]")]
public class AboutController(
    IUserService userService,
    IRoleAssignmentService roleAssignmentService,
    RoleCatalog catalog,
    IClock clock,
    ILogger<AboutController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [AllowAnonymous]
    [HttpGet("")]
    public IActionResult Index([FromServices] IWebHostEnvironment env)
    {
        // Committed snapshot, not a live API: demo must work on playa connectivity.
        DevStatsViewModel? stats = null;
        var file = env.WebRootFileProvider.GetFileInfo("data/dev-stats.json");
        if (file.Exists)
        {
            try
            {
                using var stream = file.CreateReadStream();
                stats = System.Text.Json.JsonSerializer.Deserialize<DevStatsViewModel>(
                    stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse dev-stats.json");
                stats = null;
            }

            if (stats?.Contributors is null)
            {
                stats = null;
            }
        }
        return View(stats);
    }

    [Authorize]
    [HttpGet("Staff")]
    public async Task<IActionResult> Staff()
    {
        try
        {
            var now = clock.GetCurrentInstant();

            // Load all active role assignments with user data — small user base, fits in memory
            var (assignments, _) = await roleAssignmentService.GetFilteredAsync(
                roleFilter: null, activeOnly: true, page: 1, pageSize: 500, now);

            var assigneeInfos = await _userService.GetUserInfosAsync(
                assignments.Select(ra => ra.UserId).Distinct().ToList());

            // Board first, Admin last, the section roles between them by title.
            var roleDefinitions = catalog.Roles
                .OrderBy(r => string.Equals(r.Name, RoleNames.Board, StringComparison.Ordinal) ? 0
                    : string.Equals(r.Name, RoleNames.Admin, StringComparison.Ordinal) ? 2 : 1)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase);

            var roleSections = new List<StaffRoleSectionViewModel>();

            foreach (var roleDef in roleDefinitions)
            {
                var holders = assignments
                    .Where(ra => string.Equals(ra.RoleName, roleDef.Name, StringComparison.Ordinal))
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
                        RoleName = roleDef.Name,
                        DisplayTitle = roleDef.Title,
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
