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
        _repo.GetProductNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [product.Id] = product.Name });

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
        _repo.GetProductNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [product.Id] = product.Name });

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
    public async Task RemoveLineAsync_rejects_when_line_not_in_order()
    {
        var lineId = Guid.NewGuid();
        var actualOrderId = Guid.NewGuid();
        var routeOrderId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new StoreLineContext(
                lineId, actualOrderId, Guid.NewGuid(),
                StoreOrderState.Open, new LocalDate(2026, 12, 31)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveLineAsync(routeOrderId, lineId, Guid.NewGuid()));
        await _repo.DidNotReceive().RemoveLineAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveLineAsync_rejects_when_order_not_open()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new StoreLineContext(
                lineId, orderId, Guid.NewGuid(),
                StoreOrderState.InvoiceIssued, new LocalDate(2026, 12, 31)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveLineAsync(orderId, lineId, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task RemoveLineAsync_rejects_after_orderable_until()
    {
        var lineId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        _repo.GetLineWithOrderAndProductAsync(lineId, Arg.Any<CancellationToken>())
            .Returns(new StoreLineContext(
                lineId, orderId, Guid.NewGuid(),
                StoreOrderState.Open, new LocalDate(2026, 1, 1)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveLineAsync(orderId, lineId, Guid.NewGuid()));
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

        await _service.RemoveLineAsync(orderId, lineId, actor);

        await _repo.Received(1).RemoveLineAsync(lineId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreLineRemoved, nameof(StoreOrderLine), lineId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateCounterpartyAsync_updates_even_when_order_issued()
    {
        // Per Store invariant: counterparty edits while issued are gated by the
        // auth handler (camp-lead denied, FinanceAdmin allowed). The service
        // itself is auth-free and must not state-gate this path.
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var order = new StoreOrder { Id = orderId, State = StoreOrderState.InvoiceIssued };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        await _service.UpdateCounterpartyAsync(
            orderId,
            new OrderCounterpartyInput("Acme", null, null, null, null),
            actor);

        order.CounterpartyName.Should().Be("Acme");
        await _repo.Received(1).UpdateOrderAsync(order, Arg.Any<CancellationToken>());
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
    // Catalog write paths (Task 3.2 / 3.3 / 3.4)
    // ==========================================================================

    [HumansFact]
    public async Task CreateProductAsync_persists_product_with_now_timestamps_and_audits()
    {
        var actor = Guid.NewGuid();
        StoreProduct? captured = null;
        await _repo.AddProductAsync(Arg.Do<StoreProduct>(p => captured = p), Arg.Any<CancellationToken>());

        var draft = new ProductDto(
            Guid.Empty, 2026, "Tent", "Big tent", 50m, 21m, 100m,
            new LocalDate(2026, 8, 1), IsActive: true);

        var newId = await _service.CreateProductAsync(draft, actor);

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
            AuditAction.StoreProductCreated, nameof(StoreProduct), newId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CreateProductAsync_rejects_empty_name()
    {
        var draft = new ProductDto(
            Guid.Empty, 2026, "   ", "", 10m, 21m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateProductAsync(draft, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task CreateProductAsync_rejects_negative_price()
    {
        var draft = new ProductDto(
            Guid.Empty, 2026, "Tent", "", -1m, 21m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateProductAsync(draft, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task CreateProductAsync_rejects_negative_vat()
    {
        var draft = new ProductDto(
            Guid.Empty, 2026, "Tent", "", 10m, -1m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateProductAsync(draft, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task UpdateProductAsync_mutates_fields_and_audits()
    {
        var existing = MakeProduct(name: "Old", price: 10m, vat: 5m);
        existing.CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0);
        existing.UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0);
        _repo.GetProductByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        StoreProduct? captured = null;
        await _repo.UpdateProductAsync(Arg.Do<StoreProduct>(p => captured = p), Arg.Any<CancellationToken>());

        var actor = Guid.NewGuid();
        var draft = new ProductDto(
            existing.Id, 2026, "New", "New desc", 99m, 10m, 25m,
            new LocalDate(2026, 9, 1), IsActive: true);

        await _service.UpdateProductAsync(draft, actor);

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
            AuditAction.StoreProductUpdated, nameof(StoreProduct), existing.Id,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateProductAsync_throws_when_product_missing()
    {
        _repo.GetProductByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((StoreProduct?)null);

        var draft = new ProductDto(
            Guid.NewGuid(), 2026, "Tent", "", 10m, 21m, null,
            new LocalDate(2026, 8, 1), IsActive: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateProductAsync(draft, Guid.NewGuid()));
    }

    [HumansFact]
    public async Task UpdateProductAsync_does_not_mutate_existing_lines_unit_price_snapshot()
    {
        // Snapshot semantics: a line added before a price change keeps the original price.
        var product = MakeProduct(name: "Tent", price: 50m, vat: 21m, deposit: 100m);
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var orderId = Guid.NewGuid();
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = orderId, State = StoreOrderState.Open });

        StoreOrderLine? capturedLine = null;
        await _repo.AddLineAsync(Arg.Do<StoreOrderLine>(l => capturedLine = l), Arg.Any<CancellationToken>());

        await _service.AddLineAsync(orderId, product.Id, 2, Guid.NewGuid());

        capturedLine.Should().NotBeNull();
        capturedLine!.UnitPriceSnapshot.Should().Be(50m);
        capturedLine.VatRateSnapshot.Should().Be(21m);
        capturedLine.DepositAmountSnapshot.Should().Be(100m);

        var draft = new ProductDto(
            product.Id, product.Year, product.Name, product.Description,
            UnitPriceEur: 999m, VatRatePercent: 30m, DepositAmountEur: 500m,
            product.OrderableUntil, IsActive: true);

        await _service.UpdateProductAsync(draft, Guid.NewGuid());

        // Line snapshot is set at write-time; updating the product after the fact
        // does NOT mutate the line's snapshot fields.
        capturedLine.UnitPriceSnapshot.Should().Be(50m);
        capturedLine.VatRateSnapshot.Should().Be(21m);
        capturedLine.DepositAmountSnapshot.Should().Be(100m);
    }

    [HumansFact]
    public async Task DeactivateProductAsync_marks_inactive_and_audits()
    {
        var existing = MakeProduct(name: "Tent");
        existing.IsActive = true;
        _repo.GetProductByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        StoreProduct? captured = null;
        await _repo.UpdateProductAsync(Arg.Do<StoreProduct>(p => captured = p), Arg.Any<CancellationToken>());

        var actor = Guid.NewGuid();
        await _service.DeactivateProductAsync(existing.Id, actor);

        captured.Should().NotBeNull();
        captured!.IsActive.Should().BeFalse();
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _audit.Received(1).LogAsync(
            AuditAction.StoreProductDeactivated, nameof(StoreProduct), existing.Id,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeactivateProductAsync_throws_when_product_missing()
    {
        _repo.GetProductByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((StoreProduct?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeactivateProductAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [HumansFact]
    public async Task GetActiveCatalogAsync_does_not_return_deactivated_products()
    {
        // GetActiveProductsForYearAsync filters by IsActive at the repo layer.
        // Verify the service relays that contract: a deactivated product is not in the
        // collection returned from the repo.
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StoreProduct>());

        var result = await _service.GetActiveCatalogAsync(2026);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // RecordStripePaymentAsync (Phase 6.2 — Stripe webhook ingestion)
    // ==========================================================================

    [HumansFact]
    public async Task RecordStripePaymentAsync_inserts_payment_when_payment_intent_id_is_new()
    {
        var orderId = Guid.NewGuid();
        var paymentIntentId = "pi_test_abc123";
        _repo.StripePaymentIntentExistsAsync(paymentIntentId, Arg.Any<CancellationToken>())
            .Returns(false);

        await _service.RecordStripePaymentAsync(orderId, paymentIntentId, 42.50m);

        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<StorePayment>(p =>
                p.OrderId == orderId &&
                p.AmountEur == 42.50m &&
                p.Method == StorePaymentMethod.Stripe &&
                p.StripePaymentIntentId == paymentIntentId &&
                p.RecordedByUserId == null),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RecordStripePaymentAsync_no_ops_when_payment_intent_id_already_exists()
    {
        var orderId = Guid.NewGuid();
        var paymentIntentId = "pi_test_dup";
        _repo.StripePaymentIntentExistsAsync(paymentIntentId, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.RecordStripePaymentAsync(orderId, paymentIntentId, 42.50m);

        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<StorePayment>(), Arg.Any<CancellationToken>());
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

        await _service.RecordStripePaymentAsync(orderId, "pi_x", 10m);

        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentRecorded,
            nameof(StorePayment),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            "StripeWebhook",
            orderId,
            nameof(StoreOrder));
    }

    [HumansFact]
    public async Task RecordStripePaymentAsync_rejects_non_positive_amounts()
    {
        var orderId = Guid.NewGuid();
        _repo.StripePaymentIntentExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Func<Task> act = () => _service.RecordStripePaymentAsync(orderId, "pi_x", 0m);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
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
