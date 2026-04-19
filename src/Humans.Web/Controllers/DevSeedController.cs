using Humans.Application.Configuration;
using Humans.Application.Interfaces;
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
    public async Task<IActionResult> SeedDashboard(CancellationToken cancellationToken)
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
            var result = await seeder.SeedAsync(cancellationToken);
            if (result.AlreadySeeded)
            {
                SetSuccess("Dashboard demo data already present — no changes.");
            }
            else
            {
                SetSuccess($"Dashboard demo seeded: {result.TeamsCreated} teams, {result.UsersCreated} humans, {result.ShiftsCreated} shifts, {result.SignupsCreated} signups, {result.TicketOrdersCreated} ticket orders.");
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
