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
