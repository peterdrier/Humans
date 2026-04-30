using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Store;

public class StoreServiceTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly StoreService _service;

    public StoreServiceTests()
    {
        _shifts.GetActiveAsync().Returns(new EventSettings
        {
            Year = 2026,
            TimeZoneId = "Europe/Madrid"
        });
        _service = new StoreService(_repo, _audit, _clock, _shifts);
    }

    // ==========================================================================
    // Read paths (Task 2.3)
    // ==========================================================================

    [HumansFact]
    public async Task GetActiveCatalogAsync_returns_empty_for_empty_catalog()
    {
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StoreProduct>());

        var result = await _service.GetActiveCatalogAsync(2026);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveCatalogAsync_maps_products_to_dtos()
    {
        var p = MakeProduct(name: "Tent", price: 50m, vat: 21m, deposit: 100m);
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new[] { p });

        var result = await _service.GetActiveCatalogAsync(2026);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Tent");
        result[0].UnitPriceEur.Should().Be(50m);
        result[0].VatRatePercent.Should().Be(21m);
        result[0].DepositAmountEur.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetOrdersForCampSeasonAsync_maps_orders_with_balance()
    {
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m);
        var campSeasonId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = campSeasonId,
            State = StoreOrderState.Open,
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = product.Id, Qty = 2,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };
        _repo.GetOrdersForCampSeasonAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(new[] { order });
        _repo.GetActiveProductsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { product });

        var result = await _service.GetOrdersForCampSeasonAsync(campSeasonId);

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
            .Returns((StoreOrder?)null);

        var result = await _service.GetOrderAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetOrderAsync_maps_order_with_balance()
    {
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m);
        var orderId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            State = StoreOrderState.Open,
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = product.Id, Qty = 1,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
        _repo.GetActiveProductsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { product });

        var result = await _service.GetOrderAsync(orderId);

        result.Should().NotBeNull();
        result!.BalanceEur.Should().Be(60.50m);
        result.Lines[0].ProductName.Should().Be("Tent");
    }

    // ==========================================================================
    // Write paths (Task 2.4)
    // ==========================================================================

    [HumansFact]
    public async Task CreateOrderAsync_persists_open_order_with_now_timestamps_and_audits()
    {
        var campSeasonId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        StoreOrder? captured = null;
        await _repo.AddOrderAsync(Arg.Do<StoreOrder>(o => captured = o), Arg.Any<CancellationToken>());

        var orderId = await _service.CreateOrderAsync(campSeasonId, "First order", actor);

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(orderId);
        captured.CampSeasonId.Should().Be(campSeasonId);
        captured.Label.Should().Be("First order");
        captured.State.Should().Be(StoreOrderState.Open);
        captured.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _audit.Received(1).LogAsync(
            AuditAction.StoreOrderCreated, nameof(StoreOrder), orderId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddLineAsync_rejects_non_positive_qty()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.AddLineAsync(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.AddLineAsync(Guid.NewGuid(), Guid.NewGuid(), -3, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task AddLineAsync_rejects_when_order_not_open()
    {
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = orderId, State = StoreOrderState.InvoiceIssued });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AddLineAsync(orderId, productId, 1, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task AddLineAsync_rejects_after_orderable_until()
    {
        var orderId = Guid.NewGuid();
        var product = MakeProduct(orderableUntil: new LocalDate(2026, 1, 1));
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = orderId, State = StoreOrderState.Open });
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        // _clock = 2026-03-14, so 2026-01-01 is past.

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AddLineAsync(orderId, product.Id, 1, Guid.NewGuid()));
        ex.Message.Should().Contain("deadline");
    }

    [HumansFact]
    public async Task AddLineAsync_snapshots_product_price_vat_deposit_and_audits()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var product = MakeProduct(price: 75m, vat: 10m, deposit: 50m,
            orderableUntil: new LocalDate(2026, 12, 31));
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = orderId, State = StoreOrderState.Open });
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        StoreOrderLine? captured = null;
        await _repo.AddLineAsync(Arg.Do<StoreOrderLine>(l => captured = l), Arg.Any<CancellationToken>());

        await _service.AddLineAsync(orderId, product.Id, 3, actor);

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
            AuditAction.StoreLineAdded, nameof(StoreOrderLine), captured.Id,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_rejects_when_order_not_open()
    {
        var lineId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new StoreLineContext(
                lineId, Guid.NewGuid(), Guid.NewGuid(),
                StoreOrderState.InvoiceIssued, new LocalDate(2026, 12, 31)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveLineAsync(lineId, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task RemoveLineAsync_rejects_after_orderable_until()
    {
        var lineId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new StoreLineContext(
                lineId, Guid.NewGuid(), Guid.NewGuid(),
                StoreOrderState.Open, new LocalDate(2026, 1, 1)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveLineAsync(lineId, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task RemoveLineAsync_removes_and_audits()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new StoreLineContext(
                lineId, orderId, Guid.NewGuid(),
                StoreOrderState.Open, new LocalDate(2026, 12, 31)));

        await _service.RemoveLineAsync(lineId, actor);

        await _repo.Received(1).RemoveLineAsync(lineId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreLineRemoved, nameof(StoreOrderLine), lineId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_rejects_when_order_not_open()
    {
        var orderId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = orderId, State = StoreOrderState.InvoiceIssued });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateCounterpartyAsync(
                orderId,
                new OrderCounterpartyInput("X", null, null, null, null),
                Guid.NewGuid()));
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_updates_fields_and_audits()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var order = new StoreOrder { Id = orderId, State = StoreOrderState.Open };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        await _service.UpdateCounterpartyAsync(
            orderId,
            new OrderCounterpartyInput("Acme", "ESB12345678", "1 St", "ES", "ops@acme.test"),
            actor);

        order.CounterpartyName.Should().Be("Acme");
        order.CounterpartyVatId.Should().Be("ESB12345678");
        order.CounterpartyAddress.Should().Be("1 St");
        order.CounterpartyCountryCode.Should().Be("ES");
        order.CounterpartyEmail.Should().Be("ops@acme.test");
        order.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _repo.Received(1).UpdateOrderAsync(order, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreCounterpartyEdited, nameof(StoreOrder), orderId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static StoreProduct MakeProduct(
        string name = "Test product",
        decimal price = 10m,
        decimal vat = 21m,
        decimal? deposit = null,
        LocalDate? orderableUntil = null,
        int year = 2026)
    {
        return new StoreProduct
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
}
