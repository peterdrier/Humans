using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Store;

public class StoreSummaryAggregateTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly StoreService _service;

    public StoreSummaryAggregateTests()
    {
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _service = new StoreService(_repo, _audit, _camps, _teams, _clock, _shifts, _stripe, NullLogger<StoreService>.Instance);
    }

    [HumansFact]
    public async Task Empty_year_returns_empty_projections()
    {
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.Year.Should().Be(2026);
        result.ByCounterparty.Should().BeEmpty();
        result.ByItem.Should().BeEmpty();
        result.CrossTab.Products.Should().BeEmpty();
        result.CrossTab.Counterparties.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Single_order_single_line_projects_to_all_three_views()
    {
        var seasonId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfo(seasonId, "Camp Alpha", "alpha")]);

        var product = new StoreProduct
        {
            Id = productId,
            Year = 2026,
            Name = "Tent",
            Description = "x",
            UnitPriceEur = 10m,
            VatRatePercent = 21m,
            DepositAmountEur = null,
            OrderableUntil = new LocalDate(2026, 12, 31),
            IsActive = true
        };
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([product]);

        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = seasonId,
            State = StoreOrderState.Open,
            Lines =
            {
                new StoreOrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    ProductId = productId,
                    Qty = 3,
                    UnitPriceSnapshot = 10m,
                    VatRateSnapshot = 21m,
                    DepositAmountSnapshot = null
                }
            },
            Payments =
            {
                new StorePayment
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    AmountEur = 20m,
                    Method = StorePaymentMethod.Manual,
                    ReceivedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
                }
            }
        };
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(seasonId)),
                Arg.Any<CancellationToken>())
            .Returns([order]);

        var result = await _service.GetStoreSummaryAsync(2026);

        // By-counterparty
        result.ByCounterparty.Should().HaveCount(1);
        var camp = result.ByCounterparty[0];
        camp.OrderId.Should().Be(orderId);
        camp.CounterpartyName.Should().Be("Camp Alpha");
        camp.CounterpartyType.Should().Be(StoreOrderCounterpartyType.Camp);
        camp.TotalDueEur.Should().Be(36.30m); // 3 * 10 + VAT 6.30
        camp.PaymentsTotalEur.Should().Be(20m);
        camp.BalanceEur.Should().Be(16.30m);

        // By-item
        result.ByItem.Should().HaveCount(1);
        result.ByItem[0].ProductId.Should().Be(productId);
        result.ByItem[0].TotalQty.Should().Be(3);
        result.ByItem[0].TotalRevenueEur.Should().Be(36.30m);

        // Cross-tab
        result.CrossTab.Products.Should().HaveCount(1);
        result.CrossTab.Products[0].TotalQty.Should().Be(3);
        result.CrossTab.Counterparties.Should().HaveCount(1);
        result.CrossTab.Counterparties[0].CounterpartyName.Should().Be("Camp Alpha");
        result.CrossTab.Counterparties[0].TotalQty.Should().Be(3);
        result.CrossTab.Counterparties[0].QtyByProduct[productId].Should().Be(3);
    }

    [HumansFact]
    public async Task Multiple_camps_and_products_produce_consistent_totals()
    {
        var (seasonA, seasonB) = (Guid.NewGuid(), Guid.NewGuid());
        var (productX, productY) = (Guid.NewGuid(), Guid.NewGuid());

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([
                MakeCampInfo(seasonA, "Camp Alpha", "alpha"),
                MakeCampInfo(seasonB, "Camp Bravo", "bravo")
            ]);

        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new StoreProduct { Id = productX, Year = 2026, Name = "X", Description = "x",
                UnitPriceEur = 5m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = true },
            new StoreProduct { Id = productY, Year = 2026, Name = "Y", Description = "y",
                UnitPriceEur = 7m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = true }
        ]);

        StoreOrder Order(Guid id, Guid season, params (Guid pid, int qty, decimal price)[] lines)
            => new()
            {
                Id = id,
                CampSeasonId = season,
                State = StoreOrderState.Open,
                Lines = [.. lines.Select(l => new StoreOrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = id,
                    ProductId = l.pid,
                    Qty = l.qty,
                    UnitPriceSnapshot = l.price,
                    VatRateSnapshot = 0m,
                    DepositAmountSnapshot = null
                })]
            };

        var orderA = Order(Guid.NewGuid(), seasonA, (productX, 2, 5m), (productY, 1, 7m));
        var orderB = Order(Guid.NewGuid(), seasonB, (productX, 4, 5m));

        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([orderA, orderB]);

        var result = await _service.GetStoreSummaryAsync(2026);

        // By-item totals
        result.ByItem.Single(i => i.ProductId == productX).TotalQty.Should().Be(6);
        result.ByItem.Single(i => i.ProductId == productY).TotalQty.Should().Be(1);

        // Cross-tab column totals match by-item
        var colX = result.CrossTab.Products.Single(c => c.ProductId == productX);
        var colY = result.CrossTab.Products.Single(c => c.ProductId == productY);
        colX.TotalQty.Should().Be(6);
        colY.TotalQty.Should().Be(1);

        // Cross-tab row totals
        result.CrossTab.Counterparties.Single(r => string.Equals(r.CounterpartyName, "Camp Alpha", StringComparison.Ordinal)).TotalQty.Should().Be(3);
        result.CrossTab.Counterparties.Single(r => string.Equals(r.CounterpartyName, "Camp Bravo", StringComparison.Ordinal)).TotalQty.Should().Be(4);

        // Sum of all cells == sum of column totals == sum of row totals
        var cellSum = result.CrossTab.Counterparties.Sum(r => r.QtyByProduct.Values.Sum());
        cellSum.Should().Be(result.CrossTab.Products.Sum(c => c.TotalQty));
        cellSum.Should().Be(result.CrossTab.Counterparties.Sum(r => r.TotalQty));
    }

    [HumansFact]
    public async Task Deactivated_product_with_lines_still_appears()
    {
        var seasonId = Guid.NewGuid();
        var deadProductId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfo(seasonId, "Camp Z", "z")]);
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new StoreProduct { Id = deadProductId, Year = 2026, Name = "Retired", Description = "x",
                UnitPriceEur = 1m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = false }
        ]);
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([
                new StoreOrder
                {
                    Id = orderId, CampSeasonId = seasonId, State = StoreOrderState.Open,
                    Lines =
                    {
                        new StoreOrderLine
                        {
                            Id = Guid.NewGuid(), OrderId = orderId, ProductId = deadProductId,
                            Qty = 2, UnitPriceSnapshot = 1m, VatRateSnapshot = 0m
                        }
                    }
                }
            ]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.ByItem.Should().ContainSingle(i => i.ProductId == deadProductId && i.TotalQty == 2);
        result.CrossTab.Products.Should().ContainSingle(p => p.ProductId == deadProductId);
    }

    [HumansFact]
    public async Task ByCamp_balance_reflects_paid_partial_unpaid_states()
    {
        // Three camps × three payment states. BalanceEur is what drives the
        // view's paid/partial/unpaid classification dropdown — keep it honest.
        var (seasonPaid, seasonPartial, seasonUnpaid) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (orderPaid, orderPartial, orderUnpaid) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var productId = Guid.NewGuid();

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([
                MakeCampInfo(seasonPaid, "Paid", "p"),
                MakeCampInfo(seasonPartial, "Partial", "pa"),
                MakeCampInfo(seasonUnpaid, "Unpaid", "u")
            ]);
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new StoreProduct { Id = productId, Year = 2026, Name = "P", Description = "x",
                UnitPriceEur = 10m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = true }
        ]);

        StoreOrder Order(Guid id, Guid season, int qty, decimal paid) => new()
        {
            Id = id,
            CampSeasonId = season,
            State = StoreOrderState.Open,
            Lines =
            {
                new StoreOrderLine
                {
                    Id = Guid.NewGuid(), OrderId = id, ProductId = productId,
                    Qty = qty, UnitPriceSnapshot = 10m, VatRateSnapshot = 0m
                }
            },
            Payments = paid == 0m
                ? new List<StorePayment>()
                : new List<StorePayment>
                {
                    new() { Id = Guid.NewGuid(), OrderId = id, AmountEur = paid,
                        Method = StorePaymentMethod.Manual, ReceivedAt = Instant.FromUtc(2026, 5, 1, 0, 0) }
                }
        };

        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([
                Order(orderPaid, seasonPaid, qty: 2, paid: 20m),      // due 20, paid 20  → balance  0  (paid)
                Order(orderPartial, seasonPartial, qty: 3, paid: 10m), // due 30, paid 10  → balance 20  (partial)
                Order(orderUnpaid, seasonUnpaid, qty: 1, paid: 0m)    // due 10, paid  0  → balance 10  (unpaid)
            ]);

        var result = await _service.GetStoreSummaryAsync(2026);

        var paidRow = result.ByCounterparty.Single(r => r.OrderId == orderPaid);
        paidRow.TotalDueEur.Should().Be(20m);
        paidRow.PaymentsTotalEur.Should().Be(20m);
        paidRow.BalanceEur.Should().Be(0m);

        var partialRow = result.ByCounterparty.Single(r => r.OrderId == orderPartial);
        partialRow.TotalDueEur.Should().Be(30m);
        partialRow.PaymentsTotalEur.Should().Be(10m);
        partialRow.BalanceEur.Should().Be(20m);

        var unpaidRow = result.ByCounterparty.Single(r => r.OrderId == orderUnpaid);
        unpaidRow.TotalDueEur.Should().Be(10m);
        unpaidRow.PaymentsTotalEur.Should().Be(0m);
        unpaidRow.BalanceEur.Should().Be(10m);
    }

    [HumansFact]
    public async Task Order_for_camp_season_not_in_year_is_excluded()
    {
        // Camp service returns ONE season for 2026; the repo gets that season's id.
        // If the repo somehow returns an order for a different season, we still
        // exclude it (defence-in-depth — the repo filter is the primary gate).
        var inYear = Guid.NewGuid();
        var outOfYear = Guid.NewGuid();
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfo(inYear, "InYear", "iy")]);
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([]);
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([
                new StoreOrder { Id = Guid.NewGuid(), CampSeasonId = outOfYear, State = StoreOrderState.Open }
            ]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.ByCounterparty.Should().BeEmpty();
        result.CrossTab.Counterparties.Should().BeEmpty();
    }

    private static CampInfo MakeCampInfo(Guid seasonId, string name, string slug)
    {
        var campId = Guid.NewGuid();
        return new CampInfo(
            campId,
            slug,
            "camp@example.com",
            "+34600000000",
            IsSwissCamp: false,
            TimesAtNowhere: 0,
            Seasons:
            [
                new CampSeasonInfo(
                    seasonId, campId, slug, 2026, null, name, string.Empty, string.Empty, [],
                    CampSeasonStatus.Active, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
                    0, null, null, null, 0, null, null)
            ]);
    }
}
