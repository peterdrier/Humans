using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Store;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public sealed class StoreRepositoryTests
{
    private readonly StoreRepository _repo;

    public StoreRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new StoreRepository(new TestDbContextFactory(options));
    }

    [HumansFact]
    public async Task AddProductAsync_then_GetActiveProductsForYearAsync_round_trip()
    {
        var product = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = 2026,
            Name = "Tent rental",
            Description = "Rental of a 4-person tent for the event",
            UnitPriceEur = 50m,
            VatRatePercent = 21m,
            DepositAmountEur = 100m,
            OrderableUntil = new LocalDate(2026, 6, 1),
            IsActive = true,
            CreatedAt = Instant.FromUtc(2026, 3, 1, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
        };

        await _repo.AddProductAsync(product);

        var fetched = await _repo.GetActiveProductsForYearAsync(2026);
        fetched.Should().HaveCount(1);
        fetched[0].Id.Should().Be(product.Id);
        fetched[0].Name.Should().Be("Tent rental");
        fetched[0].UnitPriceEur.Should().Be(50m);
        fetched[0].DepositAmountEur.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetActiveProductsForYearAsync_filters_by_year_and_active_flag()
    {
        await _repo.AddProductAsync(MakeProduct(year: 2026, isActive: true, name: "A"));
        await _repo.AddProductAsync(MakeProduct(year: 2026, isActive: false, name: "B"));
        await _repo.AddProductAsync(MakeProduct(year: 2025, isActive: true, name: "C"));

        var results = await _repo.GetActiveProductsForYearAsync(2026);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("A");
    }

    [HumansFact]
    public async Task GetOrCreateTreasurySyncStateAsync_creates_singleton_on_first_call()
    {
        var first = await _repo.GetOrCreateTreasurySyncStateAsync();
        var second = await _repo.GetOrCreateTreasurySyncStateAsync();

        first.Id.Should().Be(1);
        second.Id.Should().Be(1);
    }

    [HumansFact]
    public async Task AddOrderAsync_then_GetOrderWithLinesAndPaymentsAsync_round_trip()
    {
        var orderId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            Label = "Lead's first order",
            CreatedAt = Instant.FromUtc(2026, 3, 1, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
        };

        await _repo.AddOrderAsync(order);

        var fetched = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(orderId);
        fetched.Label.Should().Be("Lead's first order");
        fetched.Lines.Should().BeEmpty();
        fetched.Payments.Should().BeEmpty();
    }

    private static StoreProduct MakeProduct(int year, bool isActive, string name)
    {
        return new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = name,
            Description = "desc",
            UnitPriceEur = 10m,
            VatRatePercent = 21m,
            OrderableUntil = new LocalDate(year, 12, 31),
            IsActive = isActive,
            CreatedAt = Instant.FromUtc(year, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(year, 1, 1, 0, 0)
        };
    }
}
