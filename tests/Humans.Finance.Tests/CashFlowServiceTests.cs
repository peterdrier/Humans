using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Finance.Services;
using Humans.Tickets.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Finance.Tests;

public class CashFlowServiceTests
{
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly Guid _yearId = Guid.NewGuid();

    private CashFlowService CreateSut() => new(_budget, _tickets);

    [HumansFact]
    public void Build_Monthly_BucketsByCalendarMonth()
    {
        var year = Year(
            Item(100m, new LocalDate(2026, 1, 5)),
            Item(-30m, new LocalDate(2026, 1, 31)),
            Item(-20m, new LocalDate(2026, 2, 1)));

        var model = CashFlowService.Build(year, "monthly", 0m);

        model.Period.Should().Be("monthly");
        model.Periods.Should().HaveCount(2);
        var jan = model.Periods[0];
        jan.PeriodStart.Should().Be(new LocalDate(2026, 1, 1));
        jan.PeriodEnd.Should().Be(new LocalDate(2026, 1, 31));
        jan.IncomeTotal.Should().Be(100m);
        jan.ExpenseTotal.Should().Be(-30m);
        jan.Net.Should().Be(70m);
        var feb = model.Periods[1];
        feb.PeriodStart.Should().Be(new LocalDate(2026, 2, 1));
        feb.PeriodEnd.Should().Be(new LocalDate(2026, 2, 28));
        feb.ExpenseTotal.Should().Be(-20m);
    }

    [HumansFact]
    public void Build_Weekly_BucketsMondayToSunday()
    {
        var year = Year(
            Item(-10m, new LocalDate(2026, 3, 9)),   // Mon
            Item(-20m, new LocalDate(2026, 3, 15)),  // Sun, same week
            Item(-40m, new LocalDate(2026, 3, 16))); // Mon, next week

        var model = CashFlowService.Build(year, "weekly", 0m);

        model.Period.Should().Be("weekly");
        model.Periods.Select(p => (p.PeriodStart, p.PeriodEnd, p.ExpenseTotal)).Should().Equal(
            (new LocalDate(2026, 3, 9), new LocalDate(2026, 3, 15), -30m),
            (new LocalDate(2026, 3, 16), new LocalDate(2026, 3, 22), -40m));
    }

    [HumansFact]
    public void Build_Periods_CarryCategoryBreakdown()
    {
        var year = Year(
            Item(-10m, new LocalDate(2026, 4, 2)),
            Item(-5m, new LocalDate(2026, 4, 20)));

        var period = CashFlowService.Build(year, "monthly", 0m).Periods.Should().ContainSingle().Subject;

        period.Categories.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { GroupName = "Ops", CategoryName = "Supplies", Amount = -15m });
    }

    [HumansTheory]
    [InlineData("quarterly", "monthly")]
    [InlineData("", "monthly")]
    [InlineData("WEEKLY", "weekly")]
    [InlineData("Monthly", "monthly")]
    public void Build_Period_UnknownFallsBackToMonthly(string requested, string expected)
    {
        var year = Year(Item(-10m, new LocalDate(2026, 3, 9)), Item(-10m, new LocalDate(2026, 3, 30)));

        var model = CashFlowService.Build(year, requested, 0m);

        model.Period.Should().Be(expected);
        model.Periods.Should().HaveCount(string.Equals(expected, "weekly", StringComparison.Ordinal) ? 2 : 1);
    }

    [HumansFact]
    public void Build_RunningNet_AccumulatesInDateOrder()
    {
        var year = Year(
            Item(-50m, new LocalDate(2026, 3, 1)),
            Item(200m, new LocalDate(2026, 1, 1)),
            Item(-100m, new LocalDate(2026, 2, 1)));

        var model = CashFlowService.Build(year, "monthly", 0m);

        model.Periods.Select(p => p.RunningNet).Should().Equal(200m, 100m, 50m);
    }

    [HumansFact]
    public void Build_FundsExhausted_MarksOnlyTheFirstPeriodTheExpenseBalanceHitsZero()
    {
        // Runway 100: Jan leaves 40 (income never extends it), Feb hits -10, Mar stays below.
        var year = Year(
            Item(1000m, new LocalDate(2026, 1, 2)),
            Item(-60m, new LocalDate(2026, 1, 3)),
            Item(-50m, new LocalDate(2026, 2, 3)),
            Item(-10m, new LocalDate(2026, 3, 3)));

        var model = CashFlowService.Build(year, "monthly", 100m);

        model.Periods.Select(p => p.FundsExhausted).Should().Equal(false, true, false);
    }

    [HumansFact]
    public void Build_FundsExhausted_ExactlyZeroCounts()
    {
        var year = Year(Item(-40m, new LocalDate(2026, 1, 3)), Item(-60m, new LocalDate(2026, 2, 3)));

        var model = CashFlowService.Build(year, "monthly", 100m);

        model.Periods.Select(p => p.FundsExhausted).Should().Equal(false, true);
    }

    [HumansFact]
    public void Build_UndatedItems_GoToUnscheduledTotals()
    {
        var year = Year(
            Item(300m, null),
            Item(-120m, null),
            Item(-5m, new LocalDate(2026, 1, 1)));

        var model = CashFlowService.Build(year, "monthly", 0m);

        model.Periods.Should().ContainSingle().Which.ExpenseTotal.Should().Be(-5m);
        model.Unscheduled.IncomeTotal.Should().Be(300m);
        model.Unscheduled.ExpenseTotal.Should().Be(-120m);
        model.Unscheduled.Net.Should().Be(180m);
        model.Unscheduled.Categories.Should().ContainSingle().Which.Amount.Should().Be(180m);
    }

    [HumansFact]
    public void ComputeVatEntries_IncomeOwesVat_ExpenseReclaimsIt_SettledAfterQuarterEnd()
    {
        var groups = Year(
            Item(121m, new LocalDate(2026, 2, 10), vatRate: 21),
            Item(-110m, new LocalDate(2026, 11, 5), vatRate: 10),
            Item(500m, new LocalDate(2026, 2, 10)),        // no VAT
            Item(-242m, null, vatRate: 21)).Groups;        // undated

        var entries = CashFlowService.ComputeVatEntries(groups);

        entries.Should().Equal(
            new CashFlowService.CashFlowEntry("VAT", "VAT Liability", -21m, new LocalDate(2026, 5, 15)),
            new CashFlowService.CashFlowEntry("VAT", "VAT Credits", 10m, new LocalDate(2027, 2, 14)));
    }

    [HumansFact]
    public void Build_IncludesVatSettlementsAsScheduledEntries()
    {
        var year = Year(Item(121m, new LocalDate(2026, 2, 10), vatRate: 21));

        var model = CashFlowService.Build(year, "monthly", 0m);

        model.Periods.Select(p => p.PeriodStart).Should().Equal(new LocalDate(2026, 2, 1), new LocalDate(2026, 5, 1));
        model.Periods[1].Categories.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { GroupName = "VAT", CategoryName = "VAT Liability", Amount = -21m });
    }

    [HumansFact]
    public async Task GetGrossTicketRevenueAsync_ConfiguredEvent_ExcludesOtherEventAndUnpaidOrders()
    {
        var year = new BudgetYearDetail(_yearId, "2026", "2026", BudgetYearStatus.Active, false, [], VendorEventId: "event-2026");
        var order = Order(100m);
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                order with { VendorEventId = "event-2026" },
                order with { VendorEventId = "event-2025", IsCurrentEvent = true, TotalAmount = 999m },
                order with { VendorEventId = "event-2026", PaymentStatus = TicketPaymentStatus.Refunded, TotalAmount = 50m },
            });

        var total = await CreateSut().GetGrossTicketRevenueAsync(year, TestContext.Current.CancellationToken);

        total.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetGrossTicketRevenueAsync_UnconfiguredYear_FallsBackToCurrentEvent()
    {
        var year = new BudgetYearDetail(_yearId, "2026", "2026", BudgetYearStatus.Active, false, []);
        var order = Order(100m);
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>
            {
                order,
                order with { IsCurrentEvent = false, TotalAmount = 999m },
            });

        var total = await CreateSut().GetGrossTicketRevenueAsync(year, TestContext.Current.CancellationToken);

        total.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetActiveYearCashFlowAsync_SeedsRunwayFromGrossTicketRevenue()
    {
        _budget.GetActiveYearAsync(Arg.Any<CancellationToken>())
            .Returns(Year(Item(-150m, new LocalDate(2026, 1, 3)), Item(-50m, new LocalDate(2026, 2, 3))));
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo> { Order(200m) });

        var model = await CreateSut().GetActiveYearCashFlowAsync("monthly", TestContext.Current.CancellationToken);

        model!.Periods.Select(p => p.FundsExhausted).Should().Equal(false, true);
    }

    [HumansFact]
    public async Task GetActiveYearCashFlowAsync_NoActiveYear_ReturnsNull()
    {
        _budget.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns((BudgetYearDetail?)null);

        var model = await CreateSut().GetActiveYearCashFlowAsync("monthly", TestContext.Current.CancellationToken);

        model.Should().BeNull();
        _ = _tickets.DidNotReceive().GetTicketOrdersAsync(Arg.Any<CancellationToken>());
    }

    private BudgetYearDetail Year(params BudgetLineItemDetail[] items)
    {
        var categoryId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        return new BudgetYearDetail(_yearId, "2026", "Budget 2026", BudgetYearStatus.Active, false,
        [
            new BudgetGroupDetail(groupId, _yearId, "Ops", 0, false, false, false, null,
            [
                new BudgetCategoryDetail(categoryId, groupId, "Supplies", 0m, ExpenditureType.OpEx, null, 0, items)
            ])
        ]);
    }

    private static BudgetLineItemDetail Item(decimal amount, LocalDate? date, int vatRate = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "item", amount, null, null, date, vatRate,
            IsAutoGenerated: false, IsCashflowOnly: false, SortOrder: 0);

    private static TicketOrderInfo Order(decimal totalAmount) => new(
        Id: Guid.NewGuid(),
        VendorOrderId: $"order-{Guid.NewGuid():N}",
        BuyerName: null,
        BuyerEmail: null,
        TotalAmount: totalAmount,
        Currency: "EUR",
        DiscountCode: null,
        PaymentStatus: TicketPaymentStatus.Paid,
        VendorEventId: "event",
        PurchasedAt: Instant.FromUtc(2026, 3, 3, 10, 0),
        MatchedUserId: null,
        IsCurrentEvent: true,
        Attendees: [],
        StripeFee: null,
        ApplicationFee: null);
}
