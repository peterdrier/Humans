using Humans.Application.Configuration;
using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("dev/seed")]
public class DevSeedController(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    IServiceProvider serviceProvider,
    IUserService userService,
    ILogger<DevSeedController> logger) : HumansControllerBase(userService)
{
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
            var seeder = serviceProvider.GetRequiredService<DevelopmentBudgetSeeder>();
            var result = await seeder.SeedAsync(user.Id, cancellationToken);
            SetSuccess(result.SuccessMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed budget demo data for user {UserId}", user.Id);
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

        try
        {
            var seeder = serviceProvider.GetRequiredService<DevelopmentCampRoleSeeder>();
            var result = await seeder.SeedAsync(user.Id, cancellationToken);
            SetSuccess(result.SuccessMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed camp roles for user {UserId}", user.Id);
            SetError("Camp role seeding failed. Check logs for details.");
        }

        return RedirectToAction(nameof(AdminController.Index), "Admin");
    }

    private bool IsDevSeedEnabled()
    {
        if (environment.IsProduction())
        {
            return false;
        }

        return configuration.GetSettingValue(
            configRegistry, "DevAuth:Enabled", "Development", defaultValue: false);
    }

    [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
    [HttpPost("dashboard")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedDashboard(CancellationToken cancellationToken)
    {
        return await SeedDashboardCoreAsync(reset: false, cancellationToken);
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost("dashboard/reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetDashboard(CancellationToken cancellationToken)
    {
        return await SeedDashboardCoreAsync(reset: true, cancellationToken);
    }

    private async Task<IActionResult> SeedDashboardCoreAsync(bool reset, CancellationToken cancellationToken)
    {
        // Stricter than IsDevSeedEnabled: dashboard seed runs only on local Development
        // (ASPNETCORE_ENVIRONMENT=Development), never on QA / preview / prod.
        if (!environment.IsDevelopment() || !IsDevSeedEnabled())
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
            var seeder = serviceProvider.GetRequiredService<DevelopmentDashboardSeeder>();

            DashboardResetResult? resetResult = null;
            if (reset)
            {
                resetResult = await seeder.ResetAsync(cancellationToken);
            }

            var result = await seeder.SeedAsync(cancellationToken);

            if (result.AlreadySeeded)
            {
                SetSuccess("Dashboard demo data already present — no changes. Use reset to reseed.");
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
            logger.LogError(ex, "Failed to seed dashboard demo data.");
            SetError("Dashboard seeding failed. Check logs for details.");
        }

        return Redirect("/Shifts/Dashboard");
    }
}
