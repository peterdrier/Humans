using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

public class BudgetServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly BudgetService _service;

    public BudgetServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _service = new BudgetService(
            _dbContext,
            new FakeClock(NodaTime.Instant.FromUtc(2026, 3, 31, 12, 0)),
            NullLogger<BudgetService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task CreateLineItemAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var category = await SeedCategoryAsync();

        var act = () => _service.CreateLineItemAsync(
            category.Id,
            "Test line item",
            100m,
            null,
            null,
            null,
            vatRate,
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(22)]
    public async Task UpdateLineItemAsync_rejects_vat_rates_outside_0_to_21(int vatRate)
    {
        var category = await SeedCategoryAsync();
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = "Existing",
            Amount = 100m,
            VatRate = 0
        };
        _dbContext.BudgetLineItems.Add(lineItem);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.UpdateLineItemAsync(
            lineItem.Id,
            "Existing",
            100m,
            null,
            null,
            null,
            vatRate,
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    private async Task<BudgetCategory> SeedCategoryAsync()
    {
        var year = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = "2026",
            Name = "Budget 2026"
        };
        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = year.Id,
            BudgetYear = year,
            Name = "Departments"
        };
        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = group.Id,
            BudgetGroup = group,
            Name = "Operations"
        };

        _dbContext.BudgetYears.Add(year);
        _dbContext.BudgetGroups.Add(group);
        _dbContext.BudgetCategories.Add(category);
        await _dbContext.SaveChangesAsync();

        return category;
    }
}
