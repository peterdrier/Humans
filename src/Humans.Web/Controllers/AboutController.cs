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
    ISystemRoleService systemRoleService,
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
        try
        {
            var file = env.WebRootFileProvider.GetFileInfo("data/dev-stats.json");
            if (file.Exists)
            {
                using var stream = file.CreateReadStream();
                stats = System.Text.Json.JsonSerializer.Deserialize<DevStatsViewModel>(
                    stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to read dev-stats.json");
            stats = null;
        }

        if (stats?.Contributors is null)
        {
            stats = null;
        }
        return View(stats);
    }

    private static readonly string[] EffortOrder = ["low", "medium", "high", "xhigh", "max"];

    [AllowAnonymous]
    [HttpGet("Logos")]
    public IActionResult Logos([FromServices] IWebHostEnvironment env)
    {
        // Committed files, one folder per drawing; no index file so parallel entries never conflict.
        var files = env.WebRootFileProvider;
        var entries = new List<LogoLibraryEntry>();
        foreach (var dir in files.GetDirectoryContents("img/logo-library").Where(d => d.IsDirectory))
        {
            var file = files.GetFileInfo($"img/logo-library/{dir.Name}/entry.json");
            if (!file.Exists)
            {
                continue;
            }
            try
            {
                using var stream = file.CreateReadStream();
                var entry = System.Text.Json.JsonSerializer.Deserialize<LogoLibraryEntry>(
                    stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entry is not null)
                {
                    entries.Add(entry with { Slug = dir.Name });
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Failed to read logo library entry {Slug}", dir.Name);
            }
        }

        var ordered = entries
            .OrderBy(e => e.Date, StringComparer.Ordinal)
            .ThenBy(e => e.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => Array.IndexOf(EffortOrder, e.Effort?.ToLowerInvariant()))
            .ToList();
        return View(ordered);
    }

    [Authorize]
    [HttpGet("Staff")]
    public async Task<IActionResult> Staff()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var now = clock.GetCurrentInstant();

            // Load all active role assignments with user data — small user base, fits in memory
            var (assignments, _) = await systemRoleService.GetFilteredAsync(
                roleFilter: null, activeOnly: true, page: 1, pageSize: 500, now, ct);

            var assigneeInfos = await _userService.GetUserInfosAsync(
                assignments.Select(ra => ra.UserId), ct);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load staff page");
            return View(new StaffViewModel { RoleSections = [] });
        }
    }
}
