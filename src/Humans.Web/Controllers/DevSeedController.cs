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
    private readonly DevelopmentBudgetSeeder _budgetSeeder;
    private readonly DevelopmentTicketSeeder _ticketSeeder;

    public DevSeedController(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ConfigurationRegistry configRegistry,
        DevelopmentBudgetSeeder budgetSeeder,
        DevelopmentTicketSeeder ticketSeeder,
        UserManager<User> userManager)
        : base(userManager)
    {
        _environment = environment;
        _configuration = configuration;
        _configRegistry = configRegistry;
        _budgetSeeder = budgetSeeder;
        _ticketSeeder = ticketSeeder;
    }

    [Authorize(Roles = RoleGroups.FinanceAdminOrAdmin)]
    [HttpGet("budget")]
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

        var result = await _budgetSeeder.SeedAsync(user.Id, cancellationToken);

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

    [Authorize(Roles = RoleGroups.TicketAdminBoardOrAdmin + "," + RoleNames.FinanceAdmin)]
    [HttpGet("tickets")]
    public async Task<IActionResult> SeedTickets(CancellationToken cancellationToken)
    {
        if (!IsDevSeedEnabled())
        {
            return NotFound();
        }

        var result = await _ticketSeeder.SeedAsync(cancellationToken);

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
