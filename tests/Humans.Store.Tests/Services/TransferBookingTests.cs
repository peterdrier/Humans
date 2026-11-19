using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Store.Tests.Services;

/// <summary>Team orders book as month-end internal transfers: Dr department / Cr the product's transfer account, the
/// change since the last entry, keyed so a rerun never books a month twice.</summary>
public class TransferBookingTests
{
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Ice = Guid.NewGuid();
    private static readonly Guid DeptCategory = Guid.NewGuid();
    private const int DeptAccount = 62900105;
    private const int TransferAccount = 62990002;

    private static AccountingOrderLineDto Line(decimal net, int month, int? transfer = TransferAccount) =>
        new(2026, Guid.NewGuid(), OrderCounterpartyType.Team, Team, "Ice Crew", null, null, null, OrderState.Open,
            null, Guid.NewGuid(), Ice, "Ice", 75900002, 62900002, transfer, 1, net, 0m, net, net, 0m, 0m,
            Instant.FromUtc(2026, month, 10, 12, 0));

    private static readonly Dictionary<Guid, int> Departments = new() { [Team] = DeptAccount };
    private static readonly Dictionary<Guid, int?> Catalog = new() { [Ice] = TransferAccount };

    private static StoreTransferBooking.BookedLine Booked(int month, decimal amount, int account = TransferAccount) =>
        new(new StoreTransferBooking.BookedKey(2026, new YearMonth(2026, month), Team, Ice), account, amount,
            "Ice Crew / Ice");

    [HumansFact]
    public void Each_month_books_the_change_since_the_last_entry_and_resumes_after_what_is_booked()
    {
        var lines = new[] { Line(40m, 5), Line(20m, 7) };
        var rows = StoreTransferBooking.Plan(2026, lines, Catalog, [Booked(5, 50m)], Departments, new YearMonth(2026, 8));

        // May is booked at 50 (since repriced to 40): June corrects it, July adds the new line.
        rows.Select(r => (r.Month, r.Amount)).Should().Equal(
            (new YearMonth(2026, 6), -10m),
            (new YearMonth(2026, 7), 20m));
        rows[0].Sides.Should().Be((TransferAccount, DeptAccount));
        rows[1].Sides.Should().Be((DeptAccount, TransferAccount));
        rows.Should().AllSatisfy(r => r.State.Should().Be(TransferState.Ready));
    }

    [HumansFact]
    public void A_team_whose_last_line_is_removed_gets_the_booked_amount_reversed()
    {
        var rows = StoreTransferBooking.Plan(2026, [], Catalog, [Booked(5, 40m)], Departments, new YearMonth(2026, 6));

        var row = rows.Single();
        (row.Month, row.Amount, row.State, row.TeamName).Should().Be(
            (new YearMonth(2026, 6), -40m, TransferState.Ready, "Ice Crew"));
        row.Sides.Should().Be((TransferAccount, DeptAccount));
    }

    [HumansFact]
    public void A_changed_transfer_account_reverses_the_old_one_and_books_the_new_one_in_full()
    {
        const int oldAccount = 62990001;
        var rows = StoreTransferBooking.Plan(
            2026, [Line(40m, 5)], Catalog, [Booked(5, 40m, oldAccount)], Departments, new YearMonth(2026, 6));

        rows.Select(r => (r.TransferAccount, r.Amount)).Should().BeEquivalentTo(
            [((int?)oldAccount, -40m), ((int?)TransferAccount, 40m)]);
    }

    [HumansFact]
    public void A_product_without_a_transfer_account_or_a_team_without_a_department_account_is_not_ready()
    {
        var rows = StoreTransferBooking.Plan(
            2026, [Line(40m, 5, transfer: null)], new Dictionary<Guid, int?>(), [], Departments,
            new YearMonth(2026, 5));
        rows.Single().State.Should().Be(TransferState.NoTransferAccount);

        rows = StoreTransferBooking.Plan(
            2026, [Line(40m, 5)], Catalog, [], new Dictionary<Guid, int>(), new YearMonth(2026, 5));
        rows.Single().State.Should().Be(TransferState.NoDepartmentAccount);
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

        var mayDescription = StoreTransferBooking.Description(2026, new YearMonth(2026, 5), Team, Ice, "Ice Crew", "Ice");
        var holded = Substitute.For<IHoldedService>();
        holded.IsConfigured.Returns(true);
        holded.GetChartAsync(Arg.Any<CancellationToken>()).Returns([]);
        holded.SyncAccountLedgerAsync(TransferAccount, Arg.Any<CancellationToken>()).Returns(true);
        holded.GetLedgerLinesAsync(TransferAccount, Arg.Any<CancellationToken>()).Returns(
            [new HoldedLedgerLineInfo(7, 2, TransferAccount, Instant.MinValue, null, mayDescription, 0m, 40m)]);

        var write = Substitute.For<IHoldedWrite>();
        write.PostJournalEntryAsync(default, 0, 0, 0m, "", default)
            .ReturnsForAnyArgs(new HoldedJournalEntryOutcome(true));

        var repo = Substitute.For<IStoreRepository>();
        repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([]);

        var sut = new StoreTransferBooking(orders, repo, budget, finance, holded, write,
            Substitute.For<IAuditLogService>(), new FakeClock(Instant.FromUtc(2026, 9, 2, 8, 0)),
            NullLogger<StoreTransferBooking>.Instance);

        var (lines, refused) = await sut.BookAsync(2026, Guid.NewGuid(), ct);

        refused.Should().BeNull();
        lines.Should().ContainSingle().Which.Outcome.Should().Be(TransferOutcome.Posted);
        await write.Received(1).PostJournalEntryAsync(new LocalDate(2026, 6, 30), DeptAccount, TransferAccount, 20m,
            StoreTransferBooking.Description(2026, new YearMonth(2026, 6), Team, Ice, "Ice Crew", "Ice"),
            Arg.Any<CancellationToken>());
    }
}
