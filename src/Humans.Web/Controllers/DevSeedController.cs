using Humans.Application.Configuration;
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

            return Ok(new
            {
                message = $"Seeded budget demo data for {result.BudgetYearName}.",
                result.BudgetYearId,
                result.BudgetYearCode,
                result.BudgetYearName,
                result.ActivatedBudgetYear,
                result.TeamsCreated,
                result.TeamsUpdated,
                result.DepartmentCategoriesSynced,
                result.GroupsCreated,
                result.CategoriesCreated,
                result.LineItemsCreated,
                financeYearDetailUrl = Url.Action(nameof(FinanceController.YearDetail), "Finance", new { id = result.BudgetYearId }),
                financeAdminUrl = Url.Action(nameof(FinanceController.Admin), "Finance")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed budget demo data for user {UserId}", user.Id);
            return StatusCode(500, new { error = "Budget seeding failed. Check logs for details." });
        }
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

        try
        {
            var seeder = _serviceProvider.GetRequiredService<DevelopmentTicketSeeder>();
            var result = await seeder.SeedAsync(cancellationToken);

            return Ok(new
            {
                message = "Seeded ticketing demo data.",
                result.PaidOrders,
                result.NonPaidOrders,
                result.OrdersCreated,
                result.AttendeesCreated,
                result.PaidTicketsSold,
                result.GrossRevenue,
                result.DonationRevenue,
                result.DiscountTotal,
                result.OrdersWithDonation,
                result.OrdersWithDiscountCode,
                result.MatchedOrders,
                result.MatchedAttendees,
                result.TwoTicketOrders,
                ticketsDashboardUrl = Url.Action(nameof(TicketController.Index), "Ticket"),
                ticketsOrdersUrl = Url.Action(nameof(TicketController.Orders), "Ticket"),
                ticketsAttendeesUrl = Url.Action(nameof(TicketController.Attendees), "Ticket")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed ticket demo data");
            return StatusCode(500, new { error = "Ticket seeding failed. Check logs for details." });
        }
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
