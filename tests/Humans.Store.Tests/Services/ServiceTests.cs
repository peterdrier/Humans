using System.Security.Claims;
using Humans.Base.Extensions;
using Humans.Store.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Teams.Contracts;
using Humans.Store.Services.Dtos;
using Humans.Stripe.Contracts;
using Humans.Base.Enums;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Store.Tests.Services;

public class ServiceTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly ISettingsService _shifts = Substitute.For<ISettingsService>();
    private readonly IStripeService _stripeService = Substitute.For<IStripeService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly IHoldedClient _holded = Substitute.For<IHoldedClient>();
    private readonly StoreSectionOptions _storeOptions = new();
    private readonly Service _service;

    public ServiceTests()
    {
        _shifts.GetActiveEventSettingsAsync().Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid"));
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _campService.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _service = new Service(_repo, _audit, _campService, _teams, _clock, _shifts, _stripeService, _holded, Options.Create(_storeOptions), NullLogger<Service>.Instance);
    }

    // ==========================================================================
    // Read paths (Task 2.3)
    // ==========================================================================

    [HumansFact]
    public async Task GetIndexDataAsync_returns_active_year_catalog_and_empty_lead_sections()
    {
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([
                MakeProduct(name: "Tent"),
                MakeProduct(name: "Blanket")
            ]);

        var result = await _service.GetIndexDataAsync(Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        result.Year.Should().Be(2026);
        result.Catalog.Select(p => p.Name).Should().Equal("Blanket", "Tent");
        result.Counterparties.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetIndexDataAsync_selects_highest_balance_camp_order_with_live_prices()
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, seasonId, "Camp Alpha", userId)
            });
        var product = MakeProduct(name: "Tent", price: 50m, vat: 0m);
        product.IsActive = false;
        var lowerBalanceId = Guid.NewGuid();
        var higherBalanceId = Guid.NewGuid();
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { seasonId })),
                Arg.Any<CancellationToken>())
            .Returns([
                new Order
                {
                    Id = lowerBalanceId, CampSeasonId = seasonId, Year = 2026,
                    Lines = { new() { Id = Guid.NewGuid(), ProductId = product.Id, Qty = 1, UnitPriceSnapshot = 1m } },
                    Payments = { new() { AmountEur = 45m, Status = PaymentStatus.Paid } }
                },
                new Order
                {
                    Id = higherBalanceId, CampSeasonId = seasonId, Year = 2026,
                    Lines = { new() { Id = Guid.NewGuid(), ProductId = product.Id, Qty = 2, UnitPriceSnapshot = 1m } },
                    Payments = { new() { AmountEur = 20m, Status = PaymentStatus.Paid } }
                }
            ]);
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([product]);
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([product]);

        var result = await _service.GetIndexDataAsync(userId, ct: TestContext.Current.CancellationToken);

        var counterparty = result.Counterparties.Should().ContainSingle().Subject;
        counterparty.CounterpartyType.Should().Be(OrderCounterpartyType.Camp);
        counterparty.CounterpartyId.Should().Be(seasonId);
        counterparty.DisplayName.Should().Be("Camp Alpha");
        var order = counterparty.Orders.Should().ContainSingle().Subject;
        order.Id.Should().Be(higherBalanceId);
        order.CounterpartyDisplayName.Should().Be("Camp Alpha");
        order.BalanceEur.Should().Be(80m);
        order.Lines.Should().ContainSingle().Subject.ProductName.Should().Be("Tent");
        await _repo.Received(1).GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetOrderPageDataAsync_loads_edit_catalog_and_computes_payment_state()
    {
        var campSeasonId = Guid.NewGuid();
        var order = new OrderDto(
            Id: Guid.NewGuid(),
            CampSeasonId: campSeasonId,
            TeamId: null,
            CounterpartyType: OrderCounterpartyType.Camp,
            CounterpartyDisplayName: "Camp Test",
            Year: 2025,
            State: OrderState.Open,
            CounterpartyName: null,
            CounterpartyVatId: null,
            CounterpartyAddress: null,
            CounterpartyCountryCode: null,
            CounterpartyEmail: null,
            IssuedInvoiceId: null,
            Lines: [],
            Payments: [],
            LinesSubtotalEur: 25m,
            VatTotalEur: 0m,
            DepositTotalEur: 0m,
            PaymentsTotalEur: 0m,
            BalanceEur: 25m,
            CreatedAt: default);
        _repo.GetActiveProductsForYearAsync(2025, Arg.Any<CancellationToken>())
            .Returns([
                MakeProduct(name: "Tent", year: 2025),
                MakeProduct(name: "Blanket", year: 2025)
            ]);
        _stripeService.IsStoreCheckoutConfigured.Returns(true);

        var result = await _service.GetOrderPageDataAsync(order, canEdit: true, canPayAuthorized: true, ct: TestContext.Current.CancellationToken);

        result.CounterpartyDisplayName.Should().Be("Camp Test");
        result.Catalog.Select(p => p.Name).Should().Equal("Blanket", "Tent");
        result.CanEdit.Should().BeTrue();
        result.CanPay.Should().BeTrue();
        result.IsStripeConfigured.Should().BeTrue();
        await _repo.Received(1).GetActiveProductsForYearAsync(2025, TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task GetActiveCatalogAsync_returns_empty_for_empty_catalog()
    {
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _service.GetActiveCatalogAsync(2026, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveCatalogAsync_maps_products_to_dtos()
    {
        var p = MakeProduct(name: "Tent", price: 50m, vat: 21m, deposit: 100m);
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([p]);

        var result = await _service.GetActiveCatalogAsync(2026, TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Tent");
        result[0].UnitPriceEur.Should().Be(50m);
        result[0].VatRatePercent.Should().Be(21m);
        result[0].DepositAmountEur.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetActiveCatalogAsync_preserves_repository_order()
    {
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([
                MakeProduct(name: "Tent"),
                MakeProduct(name: "Cup"),
                MakeProduct(name: "Blanket")
            ]);

        var result = await _service.GetActiveCatalogAsync(2026, TestContext.Current.CancellationToken);

        result.Select(p => p.Name).Should().Equal("Tent", "Cup", "Blanket");
    }

    [HumansFact]
    public async Task GetAllProductsForYearAsync_preserves_repository_order()
    {
        var activeTent = MakeProduct(name: "Tent");
        activeTent.IsActive = true;
        var inactiveBag = MakeProduct(name: "Bag");
        inactiveBag.IsActive = false;
        var activeCup = MakeProduct(name: "Cup");
        activeCup.IsActive = true;

        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([activeTent, inactiveBag, activeCup]);

        var result = await _service.GetAllProductsForYearAsync(2026, TestContext.Current.CancellationToken);

        result.Select(p => p.Name).Should().Equal("Tent", "Bag", "Cup");
    }

    [HumansFact]
    public async Task GetOrdersForCampSeasonAsync_maps_orders_with_balance()
    {
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m);
        var campSeasonId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CampSeasonId = campSeasonId,
            State = OrderState.Open,
            Lines = new List<OrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = product.Id, Qty = 2,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };
        _repo.GetOrdersForCampSeasonAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns([order]);
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });

        var result = await _service.GetOrdersForCampSeasonAsync(campSeasonId, TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].LinesSubtotalEur.Should().Be(100m);
        result[0].VatTotalEur.Should().Be(21m);
        result[0].BalanceEur.Should().Be(121m);
        result[0].Lines[0].ProductName.Should().Be("Tent");
    }

    [HumansFact]
    public async Task GetOrderAsync_returns_null_when_missing()
    {
        _repo.GetOrderWithLinesAndPaymentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await _service.GetOrderAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetOrderAsync_maps_order_with_balance()
    {
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m);
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            State = OrderState.Open,
            Lines = new List<OrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = product.Id, Qty = 1,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });

        var result = await _service.GetOrderAsync(orderId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.BalanceEur.Should().Be(60.50m);
        result.Lines[0].ProductName.Should().Be("Tent");
    }

    [HumansFact]
    public async Task GetOrderAsync_open_order_line_reflects_current_catalog_price()
    {
        // Line was added at €50/21%; the catalog price has since dropped to €40/10%.
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m);
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            State = OrderState.Open,
            Lines = new List<OrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = product.Id, Qty = 1,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });
        var repriced = MakeProduct(name: "Tent", price: 40m, vat: 10m);
        repriced.Id = product.Id;
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<Product> { repriced });

        var result = await _service.GetOrderAsync(orderId, TestContext.Current.CancellationToken);

        result!.Lines[0].EffectiveUnitPrice.Should().Be(40m);
        result.Lines[0].EffectiveVatRate.Should().Be(10m);
        result.BalanceEur.Should().Be(44m); // 40 + 10% VAT
    }

    [HumansFact]
    public async Task GetOrderAsync_issued_order_line_keeps_snapshot_price()
    {
        // After invoicing, later catalog price changes must not move the order.
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m);
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            State = OrderState.InvoiceIssued,
            Lines = new List<OrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = product.Id, Qty = 1,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });
        var repriced = MakeProduct(name: "Tent", price: 40m, vat: 10m);
        repriced.Id = product.Id;
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<Product> { repriced });

        var result = await _service.GetOrderAsync(orderId, TestContext.Current.CancellationToken);

        result!.Lines[0].EffectiveUnitPrice.Should().Be(50m); // snapshot, not current 40
        result.BalanceEur.Should().Be(60.50m);
    }

    [HumansFact]
    public async Task GetOrderAsync_does_not_repair_legacy_year_before_caller_authorizes_order()
    {
        var orderId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CampSeasonId = seasonId,
            Year = 0,
            State = OrderState.Open,
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(seasonId, "Camp Alpha", 2025));

        var result = await _service.GetOrderAsync(orderId, TestContext.Current.CancellationToken);

        result!.Year.Should().Be(0);
        await _repo.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Order>(),
            Arg.Any<CancellationToken>());
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetOrderYearRepairReportAsync_lists_resolved_and_unresolved_rows()
    {
        var resolvedOrderId = Guid.NewGuid();
        var resolvedSeasonId = Guid.NewGuid();
        var missingOrderId = Guid.NewGuid();
        var missingSeasonId = Guid.NewGuid();
        _repo.GetOrdersWithMissingYearAsync(Arg.Any<CancellationToken>()).Returns([
            new Order { Id = resolvedOrderId, CampSeasonId = resolvedSeasonId, Year = 0 },
            new Order { Id = missingOrderId, CampSeasonId = missingSeasonId, Year = 0 },
        ]);
        _campService.GetCampSeasonByIdAsync(resolvedSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(resolvedSeasonId, "Camp Alpha", 2025));
        _campService.GetCampSeasonByIdAsync(missingSeasonId, Arg.Any<CancellationToken>())
            .Returns((CampSeasonInfo?)null);

        var report = await _service.GetOrderYearRepairReportAsync(TestContext.Current.CancellationToken);

        report.Rows.Should().HaveCount(2);
        report.ResolvableCount.Should().Be(1);
        report.Rows.Should().Contain(row => row.OrderId == resolvedOrderId
            && row.CampName == "Camp Alpha"
            && row.ResolvedYear == 2025);
        report.Rows.Should().Contain(row => row.OrderId == missingOrderId
            && row.ResolvedYear == null);
        await _repo.DidNotReceive().UpdateOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RepairOrderYearsAsync_updates_and_audits_each_resolvable_row()
    {
        var actorId = Guid.NewGuid();
        var firstOrderId = Guid.NewGuid();
        var firstSeasonId = Guid.NewGuid();
        var secondOrderId = Guid.NewGuid();
        var secondSeasonId = Guid.NewGuid();
        var unresolvedOrderId = Guid.NewGuid();
        var unresolvedSeasonId = Guid.NewGuid();
        _repo.GetOrdersWithMissingYearAsync(Arg.Any<CancellationToken>()).Returns([
            new Order { Id = firstOrderId, CampSeasonId = firstSeasonId, Year = 0 },
            new Order { Id = secondOrderId, CampSeasonId = secondSeasonId, Year = 0 },
            new Order { Id = unresolvedOrderId, CampSeasonId = unresolvedSeasonId, Year = 0 },
        ]);
        _campService.GetCampSeasonByIdAsync(firstSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(firstSeasonId, "Camp Alpha", 2025));
        _campService.GetCampSeasonByIdAsync(secondSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(secondSeasonId, "Camp Beta", 2026));
        _campService.GetCampSeasonByIdAsync(unresolvedSeasonId, Arg.Any<CancellationToken>())
            .Returns((CampSeasonInfo?)null);

        var repaired = await _service.RepairOrderYearsAsync(actorId, TestContext.Current.CancellationToken);

        repaired.Should().Be(2);
        await _repo.Received(1).UpdateOrderAsync(
            Arg.Is<Order>(order => order.Id == firstOrderId && order.Year == 2025),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateOrderAsync(
            Arg.Is<Order>(order => order.Id == secondOrderId && order.Year == 2026),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateOrderAsync(
            Arg.Is<Order>(order => order.Id == unresolvedOrderId),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreOrderYearBackfilled,
            AuditEntityTypes.Order,
            firstOrderId,
            Arg.Any<string>(),
            actorId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreOrderYearBackfilled,
            AuditEntityTypes.Order,
            secondOrderId,
            Arg.Any<string>(),
            actorId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RepairOrderYearsAsync_is_idempotent_after_candidates_are_gone()
    {
        var orderId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        _repo.GetOrdersWithMissingYearAsync(Arg.Any<CancellationToken>())
            .Returns(
                [new Order { Id = orderId, CampSeasonId = seasonId, Year = 0 }],
                []);
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(seasonId, "Camp Alpha", 2025));

        var first = await _service.RepairOrderYearsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var second = await _service.RepairOrderYearsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        first.Should().Be(1);
        second.Should().Be(0);
        await _repo.Received(1).UpdateOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Write paths (Task 2.4)
    // ==========================================================================

    [HumansFact]
    public async Task CreateOrderAsync_persists_open_order_with_now_timestamps_and_audits()
    {
        var campSeasonId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        Order? captured = null;
        await _repo.AddOrderAsync(Arg.Do<Order>(o => captured = o), Arg.Any<CancellationToken>());
        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(campSeasonId, Guid.NewGuid(), "alpha", 2026, null,
                "Camp X", string.Empty, string.Empty, [], CampSeasonStatus.Pending,
                YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No, 0, null, null, null, 0, null, null));

        var creation = await _service.CreateOrderAsync(campSeasonId, actor, TestContext.Current.CancellationToken);
        creation.Succeeded.Should().BeTrue();
        creation.ErrorKey.Should().BeNull();
        var orderId = creation.CreatedId!.Value;

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(orderId);
        captured.CampSeasonId.Should().Be(campSeasonId);
        captured.State.Should().Be(OrderState.Open);
        captured.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _audit.Received(1).LogAsync(
            AuditAction.StoreOrderCreated, AuditEntityTypes.Order, orderId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CreateOrder_refuses_a_missing_season_without_writing()
    {
        var result = await _service.CreateOrderAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Store_CampSeasonMissing");
        result.CreatedId.Should().BeNull();
        await _repo.DidNotReceive().AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Order_creation_dependency_faults_propagate_unchanged(bool team)
    {
        var id = Guid.NewGuid();
        var failure = new InvalidOperationException("Private lookup diagnostic");
        _teams.GetTeamAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromException<TeamInfo?>(failure));
        _campService.GetCampSeasonByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromException<CampSeasonInfo?>(failure));
        Func<Task> action = async () =>
        {
            if (team) await _service.CreateTeamOrderAsync(id, Guid.NewGuid(), TestContext.Current.CancellationToken);
            else await _service.CreateOrderAsync(id, Guid.NewGuid(), TestContext.Current.CancellationToken);
        };
        (await Assert.ThrowsAsync<InvalidOperationException>(action)).Should().BeSameAs(failure);
        await _repo.DidNotReceive().AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddLineAsync_rejects_non_positive_qty()
    {
        foreach (var qty in new[] { 0, -3 })
        {
            var result = await _service.AddLineAsync(Guid.NewGuid(), Guid.NewGuid(), qty,
                Guid.NewGuid(), TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeFalse();
            result.ErrorKey.Should().Be("Store_QuantityPositive");
        }
        await _repo.DidNotReceive().AddLineAsync(Arg.Any<OrderLine>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddLineAsync_rejects_when_order_not_open()
    {
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new Order { Id = orderId, State = OrderState.InvoiceIssued });

        var rejection = await _service.AddLineAsync(orderId, productId, 1, Guid.NewGuid(), TestContext.Current.CancellationToken);
        rejection.Succeeded.Should().BeFalse();
        rejection.ErrorKey.Should().Be("Store_OrderLinesFrozen");
        await _repo.DidNotReceive().AddLineAsync(Arg.Any<OrderLine>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("order", "Store_OrderMissing")]
    [InlineData("product", "Store_ProductMissing")]
    [InlineData("inactive", "Store_ProductInactive")]
    public async Task AddLine_refusals_log_without_exception_and_do_not_write(string missing, string expectedKey)
    {
        var order = new Order { Id = Guid.NewGuid(), Year = 2026, State = OrderState.Open };
        var product = MakeProduct();
        product.IsActive = !string.Equals(missing, "inactive", StringComparison.Ordinal);
        _repo.GetOrderByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(string.Equals(missing, "order", StringComparison.Ordinal) ? null : order);
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(string.Equals(missing, "product", StringComparison.Ordinal) ? null : product);
        var logger = Substitute.For<ILogger<Service>>();
        var service = new Service(_repo, _audit, _campService, _teams, _clock, _shifts, _stripeService,
            _holded, Options.Create(_storeOptions), logger);

        var result = await service.AddLineAsync(order.Id, product.Id, 1, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be(expectedKey);
        await _repo.DidNotReceive().AddLineAsync(Arg.Any<OrderLine>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
        logger.ReceivedCalls().Should().ContainSingle(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning && call.GetArguments()[3] == null);
    }

    [HumansFact]
    public async Task AddLineAsync_allows_past_deadline_and_notes_it_in_audit()
    {
        // The deadline gate is authorization (OrderAuthorizationHandler) — the
        // service is auth-free and only annotates the audit trail.
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var product = MakeProduct(orderableUntil: new LocalDate(2026, 1, 1));
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new Order { Id = orderId, Year = 2026, State = OrderState.Open });
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        // _clock = 2026-03-14, so 2026-01-01 is past.

        await _service.AddLineAsync(orderId, product.Id, 1, actor, TestContext.Current.CancellationToken);

        await _repo.Received(1).AddLineAsync(
            Arg.Is<OrderLine>(l => l.OrderId == orderId && l.ProductId == product.Id),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreLineAdded, AuditEntityTypes.OrderLine, Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("past order deadline")), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddLineAsync_snapshots_product_price_vat_deposit_and_audits()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var product = MakeProduct(price: 75m, vat: 10m, deposit: 50m,
            orderableUntil: new LocalDate(2026, 12, 31));
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new Order { Id = orderId, Year = 2026, State = OrderState.Open });
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        OrderLine? captured = null;
        await _repo.AddLineAsync(Arg.Do<OrderLine>(l => captured = l), Arg.Any<CancellationToken>());

        await _service.AddLineAsync(orderId, product.Id, 3, actor, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.OrderId.Should().Be(orderId);
        captured.ProductId.Should().Be(product.Id);
        captured.Qty.Should().Be(3);
        captured.UnitPriceSnapshot.Should().Be(75m);
        captured.VatRateSnapshot.Should().Be(10m);
        captured.DepositAmountSnapshot.Should().Be(50m);
        captured.AddedByUserId.Should().Be(actor);
        captured.AddedAt.Should().Be(_clock.GetCurrentInstant());

        await _audit.Received(1).LogAsync(
            AuditAction.StoreLineAdded, AuditEntityTypes.OrderLine, captured.Id,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [InlineData("Catalog", false, false)]
    [InlineData("Catalog", false, true)]
    [InlineData("Catalog", true, false)]
    [InlineData("Catalog", true, true)]
    [InlineData("AddLine", false, false)]
    [InlineData("AddLine", false, true)]
    [InlineData("AddLine", true, false)]
    [InlineData("AddLine", true, true)]
    [InlineData("RemoveLine", false, false)]
    [InlineData("RemoveLine", false, true)]
    [InlineData("RemoveLine", true, false)]
    [InlineData("RemoveLine", true, true)]
    [InlineData("Counterparty", false, false)]
    [InlineData("Counterparty", false, true)]
    [InlineData("Counterparty", true, false)]
    [InlineData("Counterparty", true, true)]
    public async Task MutationResults_DoNotExposePersistenceFailures(string operation, bool argumentError, bool lookupFailure)
    {
        Exception failure = argumentError ? new ArgumentException("Private persistence diagnostic", nameof(operation))
            : new InvalidOperationException("Private persistence diagnostic");
        var order = new Order { Id = Guid.NewGuid(), Year = 2026, State = OrderState.Open };
        var product = MakeProduct();
        var lineId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(_ => lookupFailure
            ? Task.FromException<Order?>(failure) : Task.FromResult<Order?>(order));
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(_ =>
            lookupFailure && string.Equals(operation, "Catalog", StringComparison.Ordinal)
                ? Task.FromException<Product?>(failure) : Task.FromResult<Product?>(product));
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>()).Returns(_ => lookupFailure
            ? Task.FromException<LineContext?>(failure)
            : Task.FromResult<LineContext?>(new LineContext(lineId, order.Id, product.Id,
                OrderState.Open, product.OrderableUntil)));
        _repo.UpdateProductAsync(product, Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        _repo.AddLineAsync(Arg.Any<OrderLine>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        _repo.RemoveLineAsync(lineId, Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        _repo.UpdateOrderAsync(order, Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        var logger = Substitute.For<ILogger<Service>>();
        var service = new Service(_repo, _audit, _campService, _teams, _clock, _shifts, _stripeService,
            _holded, Options.Create(_storeOptions), logger);
        var ct = TestContext.Current.CancellationToken;
        bool succeeded;
        string? error;
        if (string.Equals(operation, "Catalog", StringComparison.Ordinal))
        {
            var result = await service.SaveProductWithResultAsync(new ProductSaveRequest(product.Id, 2026,
                "Tent", null, 50m, 21m, null, "2026-08-01", true, null), Guid.NewGuid(), ct);
            succeeded = result.Succeeded;
            error = result.ErrorMessage;
        }
        else
        {
            Func<Task> action = async () =>
            {
                _ = operation switch
                {
                    "AddLine" => await service.AddLineAsync(order.Id, product.Id, 1, Guid.NewGuid(), ct),
                    "RemoveLine" => await service.RemoveLineAsync(order.Id, lineId, Guid.NewGuid(), ct),
                    _ => await service.UpdateCounterpartyAsync(order.Id,
                        new OrderCounterpartyInput("Acme", null, null, null, null), Guid.NewGuid(), ct)
                };
            };
            (await Assert.ThrowsAsync(failure.GetType(), action)).Should().BeSameAs(failure);
            logger.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "Log"
                && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
            return;
        }

        succeeded.Should().BeFalse();
        error.Should().BeNull("the controller already owns the localized fallback");
        logger.ReceivedCalls().Should().ContainSingle(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Error
            && ReferenceEquals(call.GetArguments()[3], failure));
    }

    [HumansFact]
    public async Task AddLineAsync_refuses_a_product_from_another_year_without_writing()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = new Order { Id = Guid.NewGuid(), Year = 2025, State = OrderState.Open };
        var product = MakeProduct(year: 2026);
        _repo.GetOrderByIdAsync(order.Id, ct).Returns(order);
        _repo.GetProductByIdAsync(product.Id, ct).Returns(product);

        var result = await _service.AddLineAsync(order.Id, product.Id, 1, Guid.NewGuid(), ct);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Store_ProductYearMismatch");
        await _repo.DidNotReceive().AddLineAsync(Arg.Any<OrderLine>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddLineAsync_refuses_an_unresolved_legacy_year_without_writing(bool campOrder)
    {
        var ct = TestContext.Current.CancellationToken;
        var order = new Order
        {
            Id = Guid.NewGuid(), Year = 0, State = OrderState.Open,
            CampSeasonId = campOrder ? Guid.NewGuid() : null,
            TeamId = campOrder ? null : Guid.NewGuid(),
        };
        _repo.GetOrderByIdAsync(order.Id, ct).Returns(order);

        var result = await _service.AddLineAsync(order.Id, Guid.NewGuid(), 1, Guid.NewGuid(), ct);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Store_OrderYearUnresolved");
        await _repo.DidNotReceive().AddLineAsync(Arg.Any<OrderLine>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddLineAsync_returns_failure_for_expected_validation()
    {
        var result = await _service.AddLineAsync(
            Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Store_QuantityPositive");
    }

    [HumansFact]
    public async Task AddLineAsync_returns_success_when_line_is_added()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var product = MakeProduct(orderableUntil: new LocalDate(2026, 12, 31));
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new Order { Id = orderId, Year = 2026, State = OrderState.Open });
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var result = await _service.AddLineAsync(orderId, product.Id, 2, actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorKey.Should().BeNull();
        await _repo.Received(1).AddLineAsync(
            Arg.Is<OrderLine>(l => l.OrderId == orderId && l.ProductId == product.Id && l.Qty == 2),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_rejects_when_line_not_in_order()
    {
        var lineId = Guid.NewGuid();
        var actualOrderId = Guid.NewGuid();
        var routeOrderId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new LineContext(
                lineId, actualOrderId, Guid.NewGuid(),
                OrderState.Open, new LocalDate(2026, 12, 31)));

        var rejection = await _service.RemoveLineAsync(routeOrderId, lineId, Guid.NewGuid(), TestContext.Current.CancellationToken);
        rejection.Succeeded.Should().BeFalse();
        rejection.ErrorKey.Should().Be("Store_LineWrongOrder");
        await _repo.DidNotReceive().RemoveLineAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_rejects_when_order_not_open()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new LineContext(
                lineId, orderId, Guid.NewGuid(),
                OrderState.InvoiceIssued, new LocalDate(2026, 12, 31)));

        var rejection = await _service.RemoveLineAsync(orderId, lineId, Guid.NewGuid(), TestContext.Current.CancellationToken);
        rejection.Succeeded.Should().BeFalse();
        rejection.ErrorKey.Should().Be("Store_OrderLinesFrozen");
        await _repo.DidNotReceive().RemoveLineAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_allows_past_deadline_and_notes_it_in_audit()
    {
        // The deadline gate is authorization (OrderAuthorizationHandler) — the
        // service is auth-free and only annotates the audit trail.
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new LineContext(
                lineId, orderId, Guid.NewGuid(),
                OrderState.Open, new LocalDate(2026, 1, 1)));

        await _service.RemoveLineAsync(orderId, lineId, actor, TestContext.Current.CancellationToken);

        await _repo.Received(1).RemoveLineAsync(lineId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreLineRemoved, AuditEntityTypes.OrderLine, lineId,
            Arg.Is<string>(d => d.Contains("past order deadline")), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_removes_and_audits()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new LineContext(
                lineId, orderId, Guid.NewGuid(),
                OrderState.Open, new LocalDate(2026, 12, 31)));

        await _service.RemoveLineAsync(orderId, lineId, actor, TestContext.Current.CancellationToken);

        await _repo.Received(1).RemoveLineAsync(lineId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreLineRemoved, AuditEntityTypes.OrderLine, lineId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_returns_failure_for_expected_rejection()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns((LineContext?)null);

        var result = await _service.RemoveLineAsync(orderId, lineId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Store_LineMissing");
    }

    [HumansFact]
    public async Task RemoveLineAsync_returns_success_when_line_is_removed()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new LineContext(
                lineId, orderId, Guid.NewGuid(),
                OrderState.Open, new LocalDate(2026, 12, 31)));

        var result = await _service.RemoveLineAsync(orderId, lineId, actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorKey.Should().BeNull();
        await _repo.Received(1).RemoveLineAsync(lineId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_updates_even_when_order_issued()
    {
        // Per Store invariant: counterparty edits while issued are gated by the
        // auth handler (camp-lead denied, FinanceAdmin allowed). The service
        // itself is auth-free and must not state-gate this path.
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var order = new Order { Id = orderId, State = OrderState.InvoiceIssued };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        await _service.UpdateCounterpartyAsync(
            orderId,
            new OrderCounterpartyInput("Acme", null, null, null, null),
            actor, TestContext.Current.CancellationToken);

        order.CounterpartyName.Should().Be("Acme");
        await _repo.Received(1).UpdateOrderAsync(order, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_updates_fields_and_audits()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var order = new Order { Id = orderId, State = OrderState.Open };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        await _service.UpdateCounterpartyAsync(
            orderId,
            new OrderCounterpartyInput("Acme", "ESB12345678", "1 St", "ES", "ops@acme.test"),
            actor, TestContext.Current.CancellationToken);

        order.CounterpartyName.Should().Be("Acme");
        order.CounterpartyVatId.Should().Be("ESB12345678");
        order.CounterpartyAddress.Should().Be("1 St");
        order.CounterpartyCountryCode.Should().Be("ES");
        order.CounterpartyEmail.Should().Be("ops@acme.test");
        order.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _repo.Received(1).UpdateOrderAsync(order, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreCounterpartyEdited, AuditEntityTypes.Order, orderId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_returns_failure_for_expected_rejection()
    {
        var orderId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await _service.UpdateCounterpartyAsync(
            orderId,
            new OrderCounterpartyInput("Acme", null, null, null, null),
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Store_OrderMissing");
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_returns_success_when_updated()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var order = new Order { Id = orderId, State = OrderState.Open };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await _service.UpdateCounterpartyAsync(
            orderId,
            new OrderCounterpartyInput("Acme", null, null, null, null),
            actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.ErrorKey.Should().BeNull();
        order.CounterpartyName.Should().Be("Acme");
        await _repo.Received(1).UpdateOrderAsync(order, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Catalog write paths (Task 3.2 / 3.3 / 3.4)
    // ==========================================================================

    [HumansFact]
    public async Task CreateProductAsync_persists_product_with_now_timestamps_and_audits()
    {
        var actor = Guid.NewGuid();
        Product? captured = null;
        await _repo.AddProductAsync(Arg.Do<Product>(p => captured = p), Arg.Any<CancellationToken>());

        var draft = new ProductDto(
            Guid.Empty, 2026, "Tent", "Big tent", 50m, 21m, 100m,
            new LocalDate(2026, 8, 1), IsActive: true);

        var newId = await _service.CreateProductAsync(draft, actor, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(newId);
        captured.Year.Should().Be(2026);
        captured.Name.Should().Be("Tent");
        captured.Description.Should().Be("Big tent");
        captured.UnitPriceEur.Should().Be(50m);
        captured.VatRatePercent.Should().Be(21m);
        captured.DepositAmountEur.Should().Be(100m);
        captured.OrderableUntil.Should().Be(new LocalDate(2026, 8, 1));
        captured.IsActive.Should().BeTrue();
        captured.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _audit.Received(1).LogAsync(
            AuditAction.StoreProductCreated, AuditEntityTypes.Product, newId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CreateProductAsync_rejects_empty_name()
    {
        var draft = new ProductDto(
            Guid.Empty, 2026, "   ", "", 10m, 21m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        var rejection = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken));
        rejection.ParamName.Should().Be("draft");
        rejection.Message.Should().Match("Product name is required*");
    }

    [HumansFact]
    public async Task CreateProductAsync_rejects_negative_price()
    {
        var draft = new ProductDto(
            Guid.Empty, 2026, "Tent", "", -1m, 21m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        var rejection = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken));
        rejection.ParamName.Should().Be("draft");
        rejection.Message.Should().Match("Unit price cannot be negative*");
    }

    [HumansFact]
    public async Task CreateProductAsync_rejects_negative_vat()
    {
        var draft = new ProductDto(
            Guid.Empty, 2026, "Tent", "", 10m, -1m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        var rejection = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken));
        rejection.ParamName.Should().Be("draft");
        rejection.Message.Should().Match("VAT rate cannot be negative*");
    }

    [HumansFact]
    public async Task UpdateProductAsync_mutates_fields_and_audits()
    {
        var existing = MakeProduct(name: "Old", price: 10m, vat: 5m);
        existing.CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0);
        existing.UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0);
        _repo.GetProductByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        Product? captured = null;
        await _repo.UpdateProductAsync(Arg.Do<Product>(p => captured = p), Arg.Any<CancellationToken>());

        var actor = Guid.NewGuid();
        var draft = new ProductDto(
            existing.Id, 2026, "New", "New desc", 99m, 10m, 25m,
            new LocalDate(2026, 9, 1), IsActive: true);

        await _service.UpdateProductAsync(draft, actor, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(existing.Id);
        captured.Name.Should().Be("New");
        captured.Description.Should().Be("New desc");
        captured.UnitPriceEur.Should().Be(99m);
        captured.VatRatePercent.Should().Be(10m);
        captured.DepositAmountEur.Should().Be(25m);
        captured.OrderableUntil.Should().Be(new LocalDate(2026, 9, 1));
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        captured.CreatedAt.Should().Be(Instant.FromUtc(2026, 1, 1, 0, 0));

        await _audit.Received(1).LogAsync(
            AuditAction.StoreProductUpdated, AuditEntityTypes.Product, existing.Id,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateProductAsync_logs_price_changed_event_with_from_to()
    {
        var existing = MakeProduct(name: "Ice", price: 1.23m, vat: 21m, deposit: null);
        _repo.GetProductByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        string? priceDescription = null;
        await _audit.LogAsync(
            AuditAction.StoreProductPriceChanged, AuditEntityTypes.Product, existing.Id,
            Arg.Do<string>(d => priceDescription = d), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());

        var draft = new ProductDto(
            existing.Id, existing.Year, "Ice", existing.Description,
            UnitPriceEur: 2.34m, VatRatePercent: 21m, DepositAmountEur: null,
            existing.OrderableUntil, IsActive: true);

        await _service.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        priceDescription.Should().Be("Price for Ice changed from 1.23 to 2.34");
    }

    [HumansFact]
    public async Task UpdateProductAsync_no_price_event_when_price_unchanged()
    {
        var existing = MakeProduct(name: "Ice", price: 1.23m, vat: 21m, deposit: null);
        _repo.GetProductByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        // Name/VAT/deposit change but price stays the same — no price-change event.
        var draft = new ProductDto(
            existing.Id, existing.Year, "Ice Cold", existing.Description,
            UnitPriceEur: 1.23m, VatRatePercent: 30m, DepositAmountEur: 5m,
            existing.OrderableUntil, IsActive: true);

        await _service.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _audit.DidNotReceive().LogAsync(
            AuditAction.StoreProductPriceChanged, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateProductAsync_throws_when_product_missing()
    {
        _repo.GetProductByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        var draft = new ProductDto(
            Guid.NewGuid(), 2026, "Tent", "", 10m, 21m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        var rejection = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => _service.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken));
        rejection.Message.Should().Match("Product * not found*");
    }

    [HumansFact]
    public async Task UpdateProductAsync_does_not_mutate_existing_lines_unit_price_snapshot()
    {
        // Snapshot semantics: a line added before a price change keeps the original price.
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m, deposit: 100m);
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var orderId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new Order { Id = orderId, Year = 2026, State = OrderState.Open });

        OrderLine? capturedLine = null;
        await _repo.AddLineAsync(Arg.Do<OrderLine>(l => capturedLine = l), Arg.Any<CancellationToken>());

        await _service.AddLineAsync(orderId, product.Id, 2, Guid.NewGuid(), TestContext.Current.CancellationToken);

        capturedLine.Should().NotBeNull();
        capturedLine!.UnitPriceSnapshot.Should().Be(50m);
        capturedLine.VatRateSnapshot.Should().Be(21m);
        capturedLine.DepositAmountSnapshot.Should().Be(100m);

        var draft = new ProductDto(
            product.Id, product.Year, product.Name, product.Description,
            UnitPriceEur: 999m, VatRatePercent: 30m, DepositAmountEur: 500m,
            product.OrderableUntil, IsActive: true);

        await _service.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Line snapshot is set at write-time; updating the product after the fact
        // does NOT mutate the line's snapshot fields.
        capturedLine.UnitPriceSnapshot.Should().Be(50m);
        capturedLine.VatRateSnapshot.Should().Be(21m);
        capturedLine.DepositAmountSnapshot.Should().Be(100m);
    }

    [HumansFact]
    public async Task SaveProductWithResultAsync_creates_product_from_form_request()
    {
        var actor = Guid.NewGuid();
        Product? captured = null;
        await _repo.AddProductAsync(Arg.Do<Product>(p => captured = p), Arg.Any<CancellationToken>());

        var result = await _service.SaveProductWithResultAsync(
            new ProductSaveRequest(
                Id: null,
                Year: 2026,
                Name: "Tent",
                Description: "Big tent",
                UnitPriceEur: 50m,
                VatRatePercent: 21m,
                DepositAmountEur: 100m,
                OrderableUntil: "2026-08-01",
                IsActive: true,
                HoldedRevenueAccountNum: null),
            actor, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Created.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.OrderableUntil.Should().Be(new LocalDate(2026, 8, 1));
    }

    [HumansFact]
    public async Task SaveProductWithResultAsync_returns_field_error_for_invalid_date()
    {
        var result = await _service.SaveProductWithResultAsync(
            new ProductSaveRequest(
                Id: null,
                Year: 2026,
                Name: "Tent",
                Description: null,
                UnitPriceEur: 50m,
                VatRatePercent: 21m,
                DepositAmountEur: null,
                OrderableUntil: "not-a-date",
                IsActive: true,
                HoldedRevenueAccountNum: null),
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorField.Should().Be(nameof(ProductSaveRequest.OrderableUntil));
        result.ErrorMessage.Should().Contain("Invalid date");
        await _repo.DidNotReceive().AddProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeactivateProductAsync_marks_inactive_and_audits()
    {
        var existing = MakeProduct(name: "Tent");
        existing.IsActive = true;
        _repo.GetProductByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        Product? captured = null;
        await _repo.UpdateProductAsync(Arg.Do<Product>(p => captured = p), Arg.Any<CancellationToken>());

        var actor = Guid.NewGuid();
        await _service.DeactivateProductAsync(existing.Id, actor, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.IsActive.Should().BeFalse();
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _audit.Received(1).LogAsync(
            AuditAction.StoreProductDeactivated, AuditEntityTypes.Product, existing.Id,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeactivateProductAsync_throws_when_product_missing()
    {
        _repo.GetProductByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeactivateProductAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [HumansFact]
    public async Task GetActiveCatalogAsync_does_not_return_deactivated_products()
    {
        // GetActiveProductsForYearAsync filters by IsActive at the repo layer.
        // Verify the service relays that contract: a deactivated product is not in the
        // collection returned from the repo.
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _service.GetActiveCatalogAsync(2026, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // RecordStripePaymentAsync (Phase 6.2 — Stripe webhook ingestion)
    // ==========================================================================

    [HumansFact]
    public async Task CreateStripeCheckoutSessionAsync_builds_checkout_session_from_order()
    {
        var order = MakeOrderDto(balanceEur: 50m, counterpartyName: "Camp Alpha", email: "camp@example.test");
        _stripeService.IsStoreCheckoutConfigured.Returns(true);
        _stripeService.CreateCheckoutSessionAsync(
                order.Id,
                42.50m,
                "https://humans.test/Store/Order/1",
                "https://humans.test/Store/Order/1",
                "camp@example.test",
                "Nobodies Collective - Camp Alpha",
                Arg.Any<CancellationToken>())
            .Returns("https://stripe.test/session");

        var url = await _service.CreateStripeCheckoutSessionAsync(
            order,
            42.50m,
            "https://humans.test/Store/Order/1", TestContext.Current.CancellationToken);

        url.SessionUrl.Should().Be("https://stripe.test/session");
        url.ErrorKey.Should().BeNull();
    }

    [HumansFact]
    public async Task CreateStripeCheckoutSessionAsync_rejects_while_a_payment_is_pending()
    {
        // A captured-but-uncleared mandate does not count as paid, so the balance still
        // looks open — the pending row alone must stop a second payment.
        var order = MakeOrderDto(balanceEur: 50m) with
        {
            Payments =
            [
                new OrderPaymentDto(Guid.NewGuid(), 30m, PaymentMethod.Stripe, PaymentStatus.Pending, "pi_pending", null, Instant.FromUtc(2026, 5, 1, 0, 0), null)
            ]
        };
        _stripeService.IsStoreCheckoutConfigured.Returns(true);

        var result = await _service.CreateStripeCheckoutSessionAsync(order, 20m, "https://humans.test/order", TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Store_PaymentPending");
        result.SessionUrl.Should().BeNull();
        await _stripeService.DidNotReceive().CreateCheckoutSessionAsync(
            Arg.Any<Guid>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateStripeCheckoutSessionAsync_rejects_amount_above_balance()
    {
        var order = MakeOrderDto(balanceEur: 10m);
        _stripeService.IsStoreCheckoutConfigured.Returns(true);

        var result = await _service.CreateStripeCheckoutSessionAsync(order, 10.01m, "https://humans.test/order", TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Store_PaymentAboveBalance");
        result.MaximumAmount.Should().Be(10m);
        result.SessionUrl.Should().BeNull();
        await _stripeService.DidNotReceive().CreateCheckoutSessionAsync(
            Arg.Any<Guid>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateStripeCheckoutSessionAsync_rejects_when_stripe_is_unconfigured()
    {
        var order = MakeOrderDto(balanceEur: 10m);
        _stripeService.IsStoreCheckoutConfigured.Returns(false);

        var result = await _service.CreateStripeCheckoutSessionAsync(order, 5m, "https://humans.test/order", TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Store_CheckoutUnconfigured");
        result.SessionUrl.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Checkout_refuses_non_positive_amount_without_calling_Stripe(int amount)
    {
        _stripeService.IsStoreCheckoutConfigured.Returns(true);
        var result = await _service.CreateStripeCheckoutSessionAsync(MakeOrderDto(balanceEur: 10m), amount,
            "https://humans.test/order", TestContext.Current.CancellationToken);
        result.ErrorKey.Should().Be("Store_PaymentPositive");
        result.SessionUrl.Should().BeNull();
        await _stripeService.DidNotReceiveWithAnyArgs().CreateCheckoutSessionAsync(default, default,
            default!, default!, default, default!, default);
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task Pay_localizes_refusals_and_hides_Stripe_diagnostics_with_detached_write_token(string culture)
    {
        using var scope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<StoreResource>>();
        var actor = Guid.NewGuid();
        var order = new Order { Id = Guid.NewGuid(), Year = 2026, State = OrderState.Open, CounterpartyName = "Camp" };
        order.Lines.Add(new OrderLine { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = Guid.NewGuid(),
            Qty = 1, UnitPriceSnapshot = 10m });
        _repo.GetOrderWithLinesAndPaymentsAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        _stripeService.IsStoreCheckoutConfigured.Returns(true);
        var failure = new InvalidOperationException("Private Stripe diagnostic");
        _stripeService.CreateCheckoutSessionAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(failure));
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(actor, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = actor }, [], [], [], null, []));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var logger = Substitute.For<ILogger<StoreController>>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test")) };
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("https://humans.test/order");
        var controller = new StoreController(_service, _campService, authorization, users, logger, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), Url = url
        };

        (await controller.Pay(order.Id, 10.01m, TestContext.Current.CancellationToken))
            .Should().BeOfType<RedirectToActionResult>();
        controller.TempData["ErrorMessage"].Should().Be(localizer["Store_PaymentAboveBalance", 10m].Value);
        await _stripeService.DidNotReceiveWithAnyArgs().CreateCheckoutSessionAsync(default, default,
            default!, default!, default, default!, default);

        (await controller.Pay(order.Id, 5m, TestContext.Current.CancellationToken))
            .Should().BeOfType<RedirectToActionResult>();
        controller.TempData["ErrorMessage"].Should().Be(localizer["Store_CheckoutFailed"].Value);
        controller.TempData["ErrorMessage"]!.ToString().Should().NotContain("Private Stripe diagnostic");
        logger.ReceivedCalls().Should().ContainSingle(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Error && ReferenceEquals(call.GetArguments()[3], failure));
        await _stripeService.Received(1).CreateCheckoutSessionAsync(order.Id, 5m,
            "https://humans.test/order", "https://humans.test/order", Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
    }

    [HumansFact]
    public async Task RecordStripePaymentAsync_inserts_payment_when_payment_intent_id_is_new()
    {
        var orderId = Guid.NewGuid();
        var paymentIntentId = "pi_test_abc123";
        _repo.StripePaymentIntentExistsAsync(paymentIntentId, Arg.Any<CancellationToken>())
            .Returns(false);

        await _service.RecordStripePaymentAsync(orderId, paymentIntentId, 42.50m, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p =>
                p.OrderId == orderId &&
                p.AmountEur == 42.50m &&
                p.Method == PaymentMethod.Stripe &&
                p.StripePaymentIntentId == paymentIntentId &&
                p.MethodName == PaymentMethod.Stripe &&
                p.RecordedByUserId == null),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(nameof(PaymentMethod.DepositReturn), 150.00, 150.00)]
    [InlineData(nameof(PaymentMethod.Refund), 80.00, -80.00)]
    public async Task RecordAdminPaymentAsync_stores_deposit_return_positive_and_refund_negative(
        string methodName, decimal entered, decimal stored)
    {
        var method = Enum.Parse<PaymentMethod>(methodName);
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(MakeDepositOrder(orderId, depositTotal: 150m));

        await _service.RecordAdminPaymentAsync(orderId, method, entered, " re_123 ", "2 of 3 fences back", actor, TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p =>
                p.OrderId == orderId &&
                p.AmountEur == stored &&
                p.Method == method &&
                p.MethodName == method &&
                p.Status == PaymentStatus.Paid &&
                p.ExternalRef == "re_123" &&
                p.Notes == "2 of 3 fences back" &&
                p.RecordedByUserId == actor),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentRecorded, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            actor, orderId, Arg.Any<string>());
    }

    [HumansTheory]
    [InlineData(nameof(PaymentMethod.Stripe))]
    [InlineData(nameof(PaymentMethod.Manual))]
    public async Task RecordAdminPaymentAsync_rejects_other_methods(string methodName)
    {
        var method = Enum.Parse<PaymentMethod>(methodName);
        var act = () => _service.RecordAdminPaymentAsync(Guid.NewGuid(), method, 10m, null, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task RecordAdminPaymentAsync_rejects_non_positive_amount(decimal amount)
    {
        var act = () => _service.RecordAdminPaymentAsync(Guid.NewGuid(), PaymentMethod.Refund, amount, null, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task RecordAdminPaymentAsync_rejects_refund_without_reference(string? externalRef)
    {
        var act = () => _service.RecordAdminPaymentAsync(Guid.NewGuid(), PaymentMethod.Refund, 10m, externalRef, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("A refund needs a reference*");
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RecordAdminPaymentAsync_allows_deposit_return_without_reference()
    {
        var orderId = Guid.NewGuid();
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(MakeDepositOrder(orderId, depositTotal: 50m));

        await _service.RecordAdminPaymentAsync(orderId, PaymentMethod.DepositReturn, 50m, "  ", null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p => p.Method == PaymentMethod.DepositReturn && p.ExternalRef == null),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RepairPaymentMethodNamesAsync_copies_int_method_and_audits_each_row()
    {
        var actor = Guid.NewGuid();
        var stripe = new Payment { Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Method = PaymentMethod.Stripe };
        var refund = new Payment { Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Method = PaymentMethod.Refund };
        _repo.GetPaymentsMissingMethodNameAsync(Arg.Any<CancellationToken>()).Returns([stripe, refund]);

        var repaired = await _service.RepairPaymentMethodNamesAsync(actor, TestContext.Current.CancellationToken);

        repaired.Should().Be(2);
        await _repo.Received(1).SetPaymentMethodNameAsync(stripe.Id, PaymentMethod.Stripe, Arg.Any<CancellationToken>());
        await _repo.Received(1).SetPaymentMethodNameAsync(refund.Id, PaymentMethod.Refund, Arg.Any<CancellationToken>());
        await _audit.Received(2).LogAsync(
            AuditAction.StorePaymentMethodBackfilled, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            actor, Arg.Any<Guid?>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task RecordAdminPaymentAsync_rejects_team_order()
    {
        var orderId = Guid.NewGuid();
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new Order { Id = orderId, TeamId = Guid.NewGuid() });

        var act = () => _service.RecordAdminPaymentAsync(orderId, PaymentMethod.DepositReturn, 10m, null, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Team orders are non-billable.");
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RecordAdminPaymentAsync_rejects_deposit_return_beyond_deposit_still_held()
    {
        // €100 deposited, €30 already returned → at most €70 can still come back.
        var orderId = Guid.NewGuid();
        var order = MakeDepositOrder(orderId, depositTotal: 100m);
        order.Payments.Add(new Payment { Id = Guid.NewGuid(), OrderId = orderId, AmountEur = 30m, Method = PaymentMethod.DepositReturn, Status = PaymentStatus.Paid });
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        var act = () => _service.RecordAdminPaymentAsync(orderId, PaymentMethod.DepositReturn, 70.01m, null, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Deposit return of EUR 70.01 exceeds the EUR 70.00 of deposit still held*");
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());

        await _service.RecordAdminPaymentAsync(orderId, PaymentMethod.DepositReturn, 70m, null, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(Arg.Is<Payment>(p => p.AmountEur == 70m), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RecordAdminPaymentAsync_refund_has_no_cap()
    {
        var orderId = Guid.NewGuid();
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(MakeDepositOrder(orderId, depositTotal: 100m));

        await _service.RecordAdminPaymentAsync(orderId, PaymentMethod.Refund, 5000m, "re_123", null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(Arg.Is<Payment>(p => p.AmountEur == -5000m), Arg.Any<CancellationToken>());
    }

    /// <summary>An issued camp order with one line carrying exactly <paramref name="depositTotal"/> in deposit.</summary>
    private static Order MakeDepositOrder(Guid orderId, decimal depositTotal) => new()
    {
        Id = orderId,
        CampSeasonId = Guid.NewGuid(),
        State = OrderState.InvoiceIssued,
        Lines = { new OrderLine { Id = Guid.NewGuid(), OrderId = orderId, ProductId = Guid.NewGuid(), Qty = 1, UnitPriceSnapshot = 10m, VatRateSnapshot = 21m, DepositAmountSnapshot = depositTotal } },
    };

    [HumansFact]
    public async Task RecordStripePaymentAsync_no_ops_when_payment_intent_id_already_exists()
    {
        var orderId = Guid.NewGuid();
        var paymentIntentId = "pi_test_dup";
        _repo.StripePaymentIntentExistsAsync(paymentIntentId, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.RecordStripePaymentAsync(orderId, paymentIntentId, 42.50m, ct: TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RecordStripePaymentAsync_emits_audit_log_with_job_actor()
    {
        var orderId = Guid.NewGuid();
        _repo.StripePaymentIntentExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await _service.RecordStripePaymentAsync(orderId, "pi_x", 10m, ct: TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentRecorded,
            AuditEntityTypes.Payment,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            "StripeWebhook",
            orderId,
            AuditEntityTypes.Order);
    }

    [HumansFact]
    public async Task RecordStripePaymentAsync_rejects_non_positive_amounts()
    {
        var orderId = Guid.NewGuid();
        _repo.StripePaymentIntentExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Func<Task> act = () => _service.RecordStripePaymentAsync(orderId, "pi_x", 0m, ct: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [HumansFact]
    public async Task HandleStripeCheckoutWebhookEventAsync_records_completed_paid_session_as_paid()
    {
        var orderId = Guid.NewGuid();
        _repo.StripePaymentIntentExistsAsync("pi_checkout", Arg.Any<CancellationToken>())
            .Returns(false);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_checkout",
            StoreCheckoutEventKind.CheckoutSessionCompleted,
            new StoreCheckoutSessionData("cs_checkout", orderId, "pi_checkout", 42.50m, PaymentStatus: "paid")), TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p =>
                p.OrderId == orderId &&
                p.StripePaymentIntentId == "pi_checkout" &&
                p.AmountEur == 42.50m &&
                p.Status == PaymentStatus.Paid),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task HandleStripeCheckoutWebhookEventAsync_records_completed_unpaid_session_as_pending()
    {
        // SEPA / async Bizum: completed fires with payment_status "unpaid" — only the mandate is
        // captured. We record Pending so the order balance does NOT count it as paid.
        var orderId = Guid.NewGuid();
        _repo.StripePaymentIntentExistsAsync("pi_sepa", Arg.Any<CancellationToken>())
            .Returns(false);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_sepa",
            StoreCheckoutEventKind.CheckoutSessionCompleted,
            new StoreCheckoutSessionData("cs_sepa", orderId, "pi_sepa", 50m, PaymentStatus: "unpaid")), TestContext.Current.CancellationToken);

        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<Payment>(p =>
                p.OrderId == orderId &&
                p.StripePaymentIntentId == "pi_sepa" &&
                p.AmountEur == 50m &&
                p.Status == PaymentStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedCheckout_FailedRecordingPropagates_AndRetryRecordsOnce(bool paid, bool cancelled)
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var recorded = new List<Payment>();
        _repo.StripePaymentIntentExistsAsync("pi_retry", ct).Returns(_ => recorded.Count > 0);
        Exception failure = cancelled
            ? new OperationCanceledException(ct)
            : new InvalidOperationException("Payment storage unavailable");
        _repo.AddPaymentAsync(Arg.Any<Payment>(), ct).Returns(Task.FromException(failure));
        var evt = new StoreCheckoutWebhookEvent("evt_retry", StoreCheckoutEventKind.CheckoutSessionCompleted,
            new StoreCheckoutSessionData("cs_retry", orderId, "pi_retry", 42.50m, PaymentStatus: paid ? "paid" : "unpaid"));

        Func<Task> process = () => _service.HandleStripeCheckoutWebhookEventAsync(evt, ct);
        (await process.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        recorded.Should().BeEmpty();
        _repo.AddPaymentAsync(Arg.Any<Payment>(), ct).Returns(call =>
        {
            recorded.Add(call.Arg<Payment>());
            return Task.CompletedTask;
        });

        await process();
        await process();

        var payment = recorded.Should().ContainSingle().Subject;
        payment.OrderId.Should().Be(orderId);
        payment.StripePaymentIntentId.Should().Be("pi_retry");
        payment.AmountEur.Should().Be(42.50m);
        payment.Status.Should().Be(paid ? PaymentStatus.Paid : PaymentStatus.Pending);
        await _audit.Received(1).LogAsync(AuditAction.StorePaymentRecorded, AuditEntityTypes.Payment,
            payment.Id, Arg.Any<string>(), "StripeWebhook", orderId, AuditEntityTypes.Order);
    }

    [HumansFact]
    public async Task HandleStripeCheckoutWebhookEventAsync_skips_completed_checkout_when_session_is_incomplete()
    {
        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_checkout",
            StoreCheckoutEventKind.CheckoutSessionCompleted,
            new StoreCheckoutSessionData("cs_checkout", null, "pi_checkout", 42.50m, PaymentStatus: "paid")), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    // ── async-payment state machine (nobodies-collective/Humans#638) ────────────

    [HumansFact]
    public async Task AsyncPaymentSucceeded_transitions_matching_pending_to_paid()
    {
        var orderId = Guid.NewGuid();
        var pending = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = 50m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Pending,
            StripePaymentIntentId = "pi_sepa"
        };
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_sepa", Arg.Any<CancellationToken>())
            .Returns(pending);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_succeeded",
            StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded,
            new StoreCheckoutSessionData("cs_sepa", orderId, "pi_sepa", 50m)), TestContext.Current.CancellationToken);

        await _repo.Received(1).UpdatePaymentStatusAsync(pending.Id, PaymentStatus.Paid, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentSettled, AuditEntityTypes.Payment, pending.Id,
            Arg.Any<string>(), "StripeWebhook", orderId, AuditEntityTypes.Order);
    }

    [HumansFact]
    public async Task AsyncPaymentSucceeded_is_idempotent_when_already_paid()
    {
        var orderId = Guid.NewGuid();
        var paid = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = 50m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Paid,
            StripePaymentIntentId = "pi_sepa"
        };
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_sepa", Arg.Any<CancellationToken>())
            .Returns(paid);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_succeeded_redeliver",
            StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded,
            new StoreCheckoutSessionData("cs_sepa", orderId, "pi_sepa", 50m)), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().UpdatePaymentStatusAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded, true)]
    [InlineData(StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed, false)]
    public async Task AsyncPayment_out_of_order_preserves_terminal_state_after_completed_and_redelivery(
        StoreCheckoutEventKind kind, bool succeeded)
    {
        var status = succeeded ? PaymentStatus.Paid : PaymentStatus.Failed;
        var orderId = Guid.NewGuid();
        Payment? recorded = null;
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_ooo", Arg.Any<CancellationToken>())
            .Returns(_ => recorded);
        _repo.StripePaymentIntentExistsAsync("pi_ooo", Arg.Any<CancellationToken>())
            .Returns(_ => recorded is not null);
        await _repo.AddPaymentAsync(Arg.Do<Payment>(p => recorded = p), Arg.Any<CancellationToken>());
        var terminal = new StoreCheckoutWebhookEvent("evt_ooo", kind,
            new StoreCheckoutSessionData("cs_ooo", orderId, "pi_ooo", 75m));

        await _service.HandleStripeCheckoutWebhookEventAsync(terminal, TestContext.Current.CancellationToken);
        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_completed_late", StoreCheckoutEventKind.CheckoutSessionCompleted,
            new StoreCheckoutSessionData("cs_ooo", orderId, "pi_ooo", 75m, PaymentStatus: "unpaid")),
            TestContext.Current.CancellationToken);
        await _service.HandleStripeCheckoutWebhookEventAsync(terminal, TestContext.Current.CancellationToken);

        recorded.Should().NotBeNull();
        recorded!.Status.Should().Be(status);
        recorded.OrderId.Should().Be(orderId);
        recorded.StripePaymentIntentId.Should().Be("pi_ooo");
        recorded.AmountEur.Should().Be(75m);
        await _repo.Received(1).AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdatePaymentStatusAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(AuditAction.StorePaymentRecorded, AuditEntityTypes.Payment,
            recorded.Id, Arg.Is<string>(message => status != PaymentStatus.Failed || message.Contains("Failed")),
            "StripeWebhook", orderId, AuditEntityTypes.Order);
    }

    [HumansFact]
    public async Task AsyncPaymentFailed_transitions_matching_pending_to_failed()
    {
        // SEPA bounces after completed: the order returns to unpaid, NOT paid-then-reversed.
        var orderId = Guid.NewGuid();
        var pending = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = 50m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Pending,
            StripePaymentIntentId = "pi_bounce"
        };
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_bounce", Arg.Any<CancellationToken>())
            .Returns(pending);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_failed",
            StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed,
            new StoreCheckoutSessionData("cs_bounce", orderId, "pi_bounce", 50m)), TestContext.Current.CancellationToken);

        await _repo.Received(1).UpdatePaymentStatusAsync(pending.Id, PaymentStatus.Failed, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentFailed, AuditEntityTypes.Payment, pending.Id,
            Arg.Any<string>(), "StripeWebhook", orderId, AuditEntityTypes.Order);
    }

    [HumansFact]
    public async Task AsyncPaymentFailed_is_idempotent_when_already_failed()
    {
        var orderId = Guid.NewGuid();
        var failed = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = 50m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Failed,
            StripePaymentIntentId = "pi_bounce"
        };
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_bounce", Arg.Any<CancellationToken>())
            .Returns(failed);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_failed_redeliver",
            StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed,
            new StoreCheckoutSessionData("cs_bounce", orderId, "pi_bounce", 50m)), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().UpdatePaymentStatusAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AsyncPaymentFailed_with_no_row_and_missing_order_is_a_noop()
    {
        // Without order metadata the terminal failure cannot be attributed to an order.
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_nothing", Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_failed_orphan",
            StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed,
            new StoreCheckoutSessionData("cs_nothing", null, "pi_nothing", 50m)), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdatePaymentStatusAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompletedThenFailed_leaves_order_unpaid_not_reversed()
    {
        // End-to-end of the SEPA-bounce path through the public handler: completed(unpaid) records
        // Pending, then async_payment_failed flips it to Failed. The order is never marked paid.
        var orderId = Guid.NewGuid();
        Payment? recorded = null;
        _repo.StripePaymentIntentExistsAsync("pi_seq", Arg.Any<CancellationToken>())
            .Returns(_ => recorded is not null);
        await _repo.AddPaymentAsync(Arg.Do<Payment>(p => recorded = p), Arg.Any<CancellationToken>());
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_seq", Arg.Any<CancellationToken>())
            .Returns(_ => recorded);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_seq_completed",
            StoreCheckoutEventKind.CheckoutSessionCompleted,
            new StoreCheckoutSessionData("cs_seq", orderId, "pi_seq", 50m, PaymentStatus: "unpaid")), TestContext.Current.CancellationToken);

        recorded.Should().NotBeNull();
        recorded!.Status.Should().Be(PaymentStatus.Pending);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_seq_failed",
            StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed,
            new StoreCheckoutSessionData("cs_seq", orderId, "pi_seq", 50m)), TestContext.Current.CancellationToken);

        await _repo.Received(1).UpdatePaymentStatusAsync(recorded.Id, PaymentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CheckoutSessionExpired_removes_orphan_pending_payment()
    {
        var orderId = Guid.NewGuid();
        var pending = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = 50m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Pending,
            StripePaymentIntentId = "pi_expired"
        };
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_expired", Arg.Any<CancellationToken>())
            .Returns(pending);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_expired",
            StoreCheckoutEventKind.CheckoutSessionExpired,
            new StoreCheckoutSessionData("cs_expired", orderId, "pi_expired", 50m)), TestContext.Current.CancellationToken);

        await _repo.Received(1).DeletePaymentAsync(pending.Id, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentExpired, AuditEntityTypes.Payment, pending.Id,
            Arg.Any<string>(), "StripeWebhook", orderId, AuditEntityTypes.Order);
    }

    [HumansFact]
    public async Task CheckoutSessionExpired_does_not_touch_a_settled_payment()
    {
        var paid = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            AmountEur = 50m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Paid,
            StripePaymentIntentId = "pi_expired_paid"
        };
        _repo.GetPaymentByStripePaymentIntentIdAsync("pi_expired_paid", Arg.Any<CancellationToken>())
            .Returns(paid);

        await _service.HandleStripeCheckoutWebhookEventAsync(new StoreCheckoutWebhookEvent(
            "evt_expired_paid",
            StoreCheckoutEventKind.CheckoutSessionExpired,
            new StoreCheckoutSessionData("cs_expired_paid", paid.OrderId, "pi_expired_paid", 50m)), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().DeletePaymentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static Product MakeProduct(
        string name = "Test product",
        decimal price = 10m,
        decimal vat = 21m,
        decimal? deposit = null,
        LocalDate? orderableUntil = null,
        int year = 2026)
    {
        return new Product
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = name,
            Description = string.Empty,
            UnitPriceEur = price,
            VatRatePercent = vat,
            DepositAmountEur = deposit,
            OrderableUntil = orderableUntil ?? new LocalDate(2026, 12, 31),
            IsActive = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        };
    }

    private static CampInfo MakeCampInfo(Guid campId, Guid seasonId, string seasonName, Guid leadUserId) =>
        new(
            campId,
            Slug: "camp-alpha",
            ContactEmail: string.Empty,
            ContactPhone: string.Empty,
            IsSwissCamp: false,
            TimesAtNowhere: 0,
            Seasons:
            [
                new CampSeasonInfo(
                    seasonId,
                    campId,
                    "camp-alpha",
                    2026,
                    null,
                    seasonName,
                    string.Empty,
                    string.Empty,
                    [],
                    CampSeasonStatus.Active,
                    YesNoMaybe.Yes,
                    YesNoMaybe.No,
                    AdultPlayspacePolicy.No,
                    MemberCount: 0,
                    SoundZone: null,
                    SpaceRequirement: null,
                    ElectricalGrid: null,
                    EeSlotCount: 0,
                    EeGrantedCount: null,
                    JoinedMemberCount: null)
                {
                    LeadUserIds = [leadUserId]
                }
            ]);

    private static CampSeasonInfo MakeCampSeasonInfo(Guid seasonId, string name, int year) =>
        new(seasonId, Guid.NewGuid(), "camp", year, null, name, string.Empty, string.Empty, [],
            CampSeasonStatus.Active, YesNoMaybe.Yes, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, null, null, null, 0, null, null);

    private static OrderDto MakeOrderDto(
        decimal balanceEur,
        string? counterpartyName = null,
        string? email = null)
    {
        return new OrderDto(
            Id: Guid.NewGuid(),
            CampSeasonId: Guid.NewGuid(),
            TeamId: null,
            CounterpartyType: OrderCounterpartyType.Camp,
            CounterpartyDisplayName: counterpartyName ?? "Camp",
            Year: 2026,
            State: OrderState.Open,
            CounterpartyName: counterpartyName,
            CounterpartyVatId: null,
            CounterpartyAddress: null,
            CounterpartyCountryCode: null,
            CounterpartyEmail: email,
            IssuedInvoiceId: null,
            Lines: [],
            Payments: [],
            LinesSubtotalEur: balanceEur,
            VatTotalEur: 0m,
            DepositTotalEur: 0m,
            PaymentsTotalEur: 0m,
            BalanceEur: balanceEur,
            CreatedAt: default);
    }
}
