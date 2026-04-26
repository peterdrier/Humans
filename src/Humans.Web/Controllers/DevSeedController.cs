using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[Route("dev/seed")]
public class DevSeedController : HumansControllerBase
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DevSeedController> _logger;

    public DevSeedController(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ConfigurationRegistry configRegistry,
        IServiceProvider serviceProvider,
        UserManager<User> userManager,
        ILogger<DevSeedController> logger)
        : base(userManager)
    {
        _environment = environment;
        _configuration = configuration;
        _configRegistry = configRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    [HttpPost("budget")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedBudget(CancellationToken cancellationToken)
    {
        if (!IsDevSeedEnabled())
        {
            return NotFound();
        }

        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            var seeder = _serviceProvider.GetRequiredService<DevelopmentBudgetSeeder>();
            var result = await seeder.SeedAsync(user.Id, cancellationToken);
            SetSuccess($"Budget demo data seeded: {result.TeamsCreated} teams created, {result.TeamsUpdated} updated, {result.CategoriesCreated} categories, {result.LineItemsCreated} line items.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed budget demo data for user {UserId}", user.Id);
            SetError("Budget seeding failed. Check logs for details.");
        }

        return RedirectToAction(nameof(AdminController.Index), "Admin");
    }

    [Authorize(Policy = PolicyNames.CampAdminOrAdmin)]
    [HttpPost("camp-roles")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedCampRoles(CancellationToken cancellationToken)
    {
        if (!IsDevSeedEnabled())
        {
            return NotFound();
        }

        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null)
        {
            return errorResult;
        }

        var seeds = new CreateCampRoleDefinitionInput[]
        {
            new("Consent Lead", null, SlotCount: 2, MinimumRequired: 1, SortOrder: 10, IsRequired: true),
            new("LNT",          null, SlotCount: 1, MinimumRequired: 1, SortOrder: 20, IsRequired: true),
            new("Shit Ninja",   null, SlotCount: 1, MinimumRequired: 1, SortOrder: 30, IsRequired: true),
            new("Power",        null, SlotCount: 1, MinimumRequired: 0, SortOrder: 40, IsRequired: false),
            new("Build Lead",   null, SlotCount: 2, MinimumRequired: 1, SortOrder: 50, IsRequired: true),
        };

        try
        {
            var campRoleService = _serviceProvider.GetRequiredService<ICampRoleService>();
            var existing = await campRoleService.ListDefinitionsAsync(includeDeactivated: true, cancellationToken);
            var existingNames = existing.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            int created = 0;
            int skipped = 0;
            foreach (var input in seeds)
            {
                if (existingNames.Contains(input.Name))
                {
                    skipped++;
                    continue;
                }
                await campRoleService.CreateDefinitionAsync(input, user.Id, cancellationToken);
                created++;
            }

            SetSuccess($"Camp roles seeded: {created} created, {skipped} already existed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed camp roles for user {UserId}", user.Id);
            SetError("Camp role seeding failed. Check logs for details.");
        }

        return RedirectToAction(nameof(AdminController.Index), "Admin");
    }

    private bool IsDevSeedEnabled()
    {
        if (_environment.IsProduction())
        {
            return false;
        }

        return _configuration.GetSettingValue(
            _configRegistry, "DevAuth:Enabled", "Development", defaultValue: false);
    }

    [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
    [HttpPost("dashboard")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedDashboard(bool reset, CancellationToken cancellationToken)
    {
        // Stricter than IsDevSeedEnabled: dashboard seed runs only on local Development
        // (ASPNETCORE_ENVIRONMENT=Development), never on QA / preview / prod.
        if (!_environment.IsDevelopment() || !IsDevSeedEnabled())
        {
            return NotFound();
        }

        var (errorResult, _) = await RequireCurrentUserAsync();
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            var seeder = _serviceProvider.GetRequiredService<DevelopmentDashboardSeeder>();

            DashboardResetResult? resetResult = null;
            if (reset)
            {
                resetResult = await seeder.ResetAsync(cancellationToken);
            }

            var result = await seeder.SeedAsync(cancellationToken);

            if (result.AlreadySeeded)
            {
                SetSuccess("Dashboard demo data already present — no changes. Pass ?reset=true to reseed.");
            }
            else
            {
                var resetNote = resetResult is null
                    ? ""
                    : $"Reset removed {resetResult.EventsDeleted} events, {resetResult.TeamsDeleted} teams, {resetResult.UsersDeleted} humans. ";
                SetSuccess($"{resetNote}Dashboard demo seeded: {result.TeamsCreated} teams, {result.UsersCreated} humans, {result.ShiftsCreated} shifts, {result.SignupsCreated} signups.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed dashboard demo data.");
            SetError("Dashboard seeding failed. Check logs for details.");
        }

        return Redirect("/Shifts/Dashboard");
    }
}
