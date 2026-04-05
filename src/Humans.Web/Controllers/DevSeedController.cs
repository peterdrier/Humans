using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
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

    [Authorize(Roles = RoleGroups.FinanceAdminOrAdmin)]
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

        return RedirectToAction("Users", "DevLogin");
    }

    [Authorize(Roles = RoleGroups.TicketAdminBoardOrAdmin + "," + RoleNames.FinanceAdmin)]
    [HttpPost("tickets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedTickets(CancellationToken cancellationToken)
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
            var syncService = _serviceProvider.GetRequiredService<ITicketSyncService>();

            // Reset sync state so the full dataset is fetched from the stub vendor
            await syncService.ResetSyncStateForFullResyncAsync();
            var result = await syncService.SyncOrdersAndAttendeesAsync(cancellationToken);
            SetSuccess($"Ticket data synced: {result.OrdersSynced} orders, {result.AttendeesSynced} attendees.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync stub ticket data for user {UserId}", user.Id);
            SetError("Ticket sync failed. Check logs for details.");
        }

        return RedirectToAction("Users", "DevLogin");
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
}
