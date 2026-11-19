using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Store.Tests.Services;

/// <summary>Team orders book as month-end internal recharges: Dr department / Cr the product's recharge account, the
/// change since the last entry, keyed so a rerun never books a month twice.</summary>
public class RechargeBookingTests
{
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Ice = Guid.NewGuid();
    private static readonly Guid DeptCategory = Guid.NewGuid();
    private const int DeptAccount = 62900105;
    private const int RechargeAccount = 62990002;

    private static AccountingOrderLineDto Line(decimal net, int month, int? recharge = RechargeAccount) =>
        new(2026, Guid.NewGuid(), OrderCounterpartyType.Team, Team, "Ice Crew", null, null, null, OrderState.Open,
            null, Guid.NewGuid(), Ice, "Ice", 75900002, 62900002, recharge, 1, net, 0m, net, net, 0m, 0m,
            Instant.FromUtc(2026, month, 10, 12, 0));

    private static readonly Dictionary<Guid, int> Departments = new() { [Team] = DeptAccount };

    [HumansFact]
    public void Each_month_books_the_change_since_the_last_entry_and_resumes_after_what_is_booked()
    {
        var lines = new[] { Line(40m, 5), Line(20m, 7) };
        var mayBooked = new StoreRechargeBooking.BookedKey(2026, new YearMonth(2026, 5), Team, Ice);

        var rows = StoreRechargeBooking.Plan(2026, lines, [(mayBooked, 50m)], Departments, new YearMonth(2026, 8));

        // May is booked at 50 (since repriced to 40): June corrects it, July adds the new line.
        rows.Select(r => (r.Month, r.Amount)).Should().Equal(
            (new YearMonth(2026, 6), -10m),
            (new YearMonth(2026, 7), 20m));
        rows[0].Sides.Should().Be((RechargeAccount, DeptAccount));
        rows[1].Sides.Should().Be((DeptAccount, RechargeAccount));
        rows.Should().AllSatisfy(r => r.State.Should().Be(RechargeState.Ready));
    }

    [HumansFact]
    public void A_product_without_a_recharge_account_or_a_team_without_a_department_account_is_not_ready()
    {
        var rows = StoreRechargeBooking.Plan(
            2026, [Line(40m, 5, recharge: null)], [], Departments, new YearMonth(2026, 5));
        rows.Single().State.Should().Be(RechargeState.NoRechargeAccount);

        rows = StoreRechargeBooking.Plan(2026, [Line(40m, 5)], [], new Dictionary<Guid, int>(), new YearMonth(2026, 5));
        rows.Single().State.Should().Be(RechargeState.NoDepartmentAccount);
    }

    [HumansFact]
    public async Task Booking_posts_ready_months_once_and_skips_what_the_ledger_already_names()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var orders = Substitute.For<IStoreAccountingRead>();
        orders.GetOrderLinesAsync(2026, Arg.Any<CancellationToken>()).Returns([Line(40m, 5), Line(20m, 6)]);

        var budget = Substitute.For<IBudgetService>();
        budget.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(new BudgetYearDetail(
            Guid.NewGuid(), "2026", "2026", default, false,
            [
                new BudgetGroupDetail(Guid.NewGuid(), Guid.NewGuid(), "Departments", 0, false, true, false, null,
                    [new BudgetCategoryDetail(DeptCategory, Guid.NewGuid(), "Ice Crew", 0m, default, Team, 0, [])]),
            ]));
        var finance = Substitute.For<IFinanceService>();
        finance.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(
            [new CategoryAccountRow(DeptCategory, "Ice Crew", "Departments", DeptAccount, "acc", "tag", Instant.MinValue)]);

        var mayDescription = StoreRechargeBooking.Description(2026, new YearMonth(2026, 5), Team, Ice, "Ice Crew", "Ice");
        var holded = Substitute.For<IHoldedService>();
        holded.IsConfigured.Returns(true);
        holded.SyncAccountLedgerAsync(RechargeAccount, Arg.Any<CancellationToken>()).Returns(true);
        holded.GetLedgerLinesAsync(RechargeAccount, Arg.Any<CancellationToken>()).Returns(
            [new HoldedLedgerLineInfo(7, 2, RechargeAccount, Instant.MinValue, null, mayDescription, 0m, 40m)]);

        var write = Substitute.For<IHoldedWrite>();
        write.PostJournalEntryAsync(default, 0, 0, 0m, "", default)
            .ReturnsForAnyArgs(new HoldedJournalEntryOutcome(true));

        var sut = new StoreRechargeBooking(orders, budget, finance, holded, write,
            Substitute.For<IAuditLogService>(), new FakeClock(Instant.FromUtc(2026, 9, 2, 8, 0)),
            NullLogger<StoreRechargeBooking>.Instance);

        var (lines, refused) = await sut.BookAsync(2026, Guid.NewGuid(), ct);

        refused.Should().BeNull();
        lines.Should().ContainSingle().Which.Outcome.Should().Be(RechargeOutcome.Posted);
        await write.Received(1).PostJournalEntryAsync(new LocalDate(2026, 6, 30), DeptAccount, RechargeAccount, 20m,
            StoreRechargeBooking.Description(2026, new YearMonth(2026, 6), Team, Ice, "Ice Crew", "Ice"),
            Arg.Any<CancellationToken>());
    }
}
