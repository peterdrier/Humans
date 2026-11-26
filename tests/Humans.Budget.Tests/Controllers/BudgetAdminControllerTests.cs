using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Budget.Controllers;
using Humans.Budget.Services;
using Humans.Finance.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Budget.Tests.Controllers;

public class BudgetAdminControllerTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task TicketTarget_EmptyProjection_PreservesActualSales(bool activeYear)
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 11, 20, 0, 0));
        var budgets = Substitute.For<IBudgetManagement>();
        var groupId = Guid.NewGuid();
        var projection = new TicketingProjectionSnapshot(new LocalDate(2026, 3, 1), new LocalDate(2026, 8, 1), 0, 5m, 100m, 10,
            1.5m, 0.25m, 3m);
        var group = new BudgetGroupDetail(groupId, Guid.NewGuid(), "Tickets", 0,
            false, false, true, projection, []);
        var year = new BudgetYearDetail(group.BudgetYearId, "2026", "Budget", BudgetYearStatus.Active, false, [group]);
        budgets.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(year);
        budgets.GetYearByIdAsync(year.Id, Arg.Any<CancellationToken>()).Returns(year);
        budgets.GetAllYearsAsync(ct: Arg.Any<CancellationToken>()).Returns([]);
        budgets.GetActualTicketsSold(group).Returns(50);
        budgets.GetProjectedRemainingTicketsAsync(group.Id, Arg.Any<CancellationToken>()).Returns(0);
        budgets.ComputeBudgetSummaryWithAvailable(Arg.Any<IReadOnlyList<BudgetGroupDetail>>())
            .Returns(new BudgetSummaryResult { IncomeSlices = [], ExpenseSlices = [] });
        var finance = Substitute.For<IFinanceService>();
        finance.GetActualsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([]);
        var ticketing = new TicketingBudgetService(Substitute.For<ITicketService>(), budgets, clock,
            NullLogger<TicketingBudgetService>.Instance);
        var controller = new BudgetAdminController(budgets, Substitute.For<ITeamService>(), ticketing, clock,
            Substitute.For<IUserService>(), finance, NullLogger<BudgetAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = activeYear ? await controller.Index() : await controller.YearDetail(year.Id);

        result.Should().BeOfType<ViewResult>();
        ((int?)controller.ViewData["ActualTicketsSold"]).Should().Be(50);
        ((int?)controller.ViewData["TotalTickets"]).Should().Be(50);
        ((int?)controller.ViewData["ProjectedRemaining"]).Should().Be(0);
    }
}
