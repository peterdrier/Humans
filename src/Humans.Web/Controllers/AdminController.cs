using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin")]
public class AdminController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<AdminController> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly IOnboardingService _onboardingService;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly QueryStatistics _queryStatistics;

    public AdminController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<AdminController> logger,
        IWebHostEnvironment environment,
        IOnboardingService onboardingService,
        ConfigurationRegistry configRegistry,
        QueryStatistics queryStatistics)
        : base(userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _logger = logger;
        _environment = environment;
        _onboardingService = onboardingService;
        _configRegistry = configRegistry;
        _queryStatistics = queryStatistics;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpPost("Humans/{id}/Purge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PurgeHuman(Guid id)
    {
        if (_environment.IsProduction())
        {
            return NotFound();
        }

        var user = await FindUserByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserAsync();

        if (user.Id == currentUser?.Id)
        {
            SetError("You cannot purge your own account.");
            return RedirectToAction("AdminDetail", "Profile", new { id });
        }

        var displayName = user.DisplayName;

        _logger.LogWarning(
            "Admin {AdminId} purging human {HumanId} ({DisplayName}) in {Environment}",
            currentUser?.Id, id, displayName, _environment.EnvironmentName);

        // Sever OAuth link so next Google login creates a fresh user
        var logins = await _userManager.GetLoginsAsync(user);
        foreach (var login in logins)
        {
            await _userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        }

        var result = await _onboardingService.PurgeHumanAsync(id);
        if (!result.Success)
        {
            return NotFound();
        }

        SetSuccess($"Purged {displayName}. They will get a fresh account on next login.");
        return RedirectToAction("AdminList", "Profile");
    }

    [HttpGet("Logs")]
    public IActionResult Logs(int count = 50)
    {
        count = Math.Clamp(count, 1, 200);
        var events = Web.Infrastructure.InMemoryLogSink.Instance.GetEvents(count);
        return View(events);
    }

    [HttpGet("Configuration")]
    public IActionResult Configuration()
    {
        var entries = _configRegistry.GetAll();

        var items = entries.Select(e =>
        {
            string? displayValue;
            if (!e.IsSet)
            {
                displayValue = "(not set)";
            }
            else if (e.IsSensitive)
            {
                // Show first 4 chars so you can tell which key is in use;
                // fully mask only very short values (≤4 chars) where the prefix IS the secret
                displayValue = e.Value switch
                {
                    { Length: > 4 } v => v[..4] + "••••••",
                    not null => "••••••",
                    _ => "••••••"
                };
            }
            else
            {
                displayValue = e.Value ?? "(set)";
            }

            return new ConfigurationItemViewModel
            {
                Section = e.Section,
                Key = e.Key,
                IsSet = e.IsSet,
                DisplayValue = displayValue,
                IsSensitive = e.IsSensitive,
                Importance = e.Importance switch
                {
                    ConfigurationImportance.Critical => "critical",
                    ConfigurationImportance.Recommended => "recommended",
                    _ => "optional"
                },
            };
        }).ToList();

        return View(new AdminConfigurationViewModel { Items = items });
    }

    // Intentionally anonymous: exposes only migration names and counts (no sensitive data).
    // Used by dev tooling to check which migrations have been applied in QA/prod,
    // so old migrations can be safely squashed and removed from the repo.
    [HttpGet("DbVersion")]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<IActionResult> DbVersion()
    {
        var applied = (await _dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = await _dbContext.Database.GetPendingMigrationsAsync();

        return Ok(new
        {
            lastApplied = applied.LastOrDefault(),
            appliedCount = applied.Count,
            pendingCount = pending.Count()
        });
    }

    [HttpGet("DbStats")]
    public IActionResult DbStats()
    {
        try
        {
            var snapshot = _queryStatistics.GetSnapshot();
            var model = new DbStatsViewModel
            {
                TotalQueryCount = _queryStatistics.TotalCount,
                Entries = snapshot.Select(e => new DbStatEntryViewModel
                {
                    Operation = e.Operation,
                    Table = e.Table,
                    Count = e.Count,
                    AverageMs = Math.Round(e.AverageMilliseconds, 2),
                    MaxMs = Math.Round(e.MaxMilliseconds, 2),
                    TotalMs = Math.Round(e.TotalMilliseconds, 2)
                }).ToList()
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading DB stats");
            SetError("Failed to load database statistics.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("DbStats/Reset")]
    [ValidateAntiForgeryToken]
    public IActionResult ResetDbStats()
    {
        try
        {
            _queryStatistics.Reset();
            _logger.LogInformation("Admin reset DB query statistics");
            SetSuccess("Query statistics have been reset.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting DB stats");
            SetError("Failed to reset database statistics.");
        }

        return RedirectToAction(nameof(DbStats));
    }

    [HttpPost("ClearHangfireLocks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearHangfireLocks()
    {
        var deleted = await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock");

        _logger.LogWarning("Admin cleared {Count} stale Hangfire locks", deleted);
        SetSuccess($"Cleared {deleted} Hangfire lock(s). Restart the app to re-register recurring jobs.");
        return RedirectToAction(nameof(Index));
    }
}
