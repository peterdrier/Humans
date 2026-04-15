using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class BudgetServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly BudgetService _service;
    private readonly ClaimsPrincipal _financeAdmin;

    public BudgetServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);

        // Default: authorize succeeds. Tests for authorization denial override this per-test.
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        _service = new BudgetService(
            _dbContext,
            _authorizationService,
            new FakeClock(NodaTime.Instant.FromUtc(2026, 3, 31, 12, 0)),
            NullLogger<BudgetService>.Instance);

        _financeAdmin = CreateUserWithRoles(RoleNames.FinanceAdmin);
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
            Guid.NewGuid(),
            _financeAdmin);

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
            Guid.NewGuid(),
            _financeAdmin);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 0 and 21*");
    }

    // ───────────────────────── Authorization enforcement ─────────────────────────

    [Fact]
    public async Task CreateLineItemAsync_Unauthorized_ThrowsBeforeMutating()
    {
        var category = await SeedCategoryAsync();
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.CreateLineItemAsync(
            category.Id, "Blocked", 100m, null, null, null, 0, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _dbContext.BudgetLineItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateLineItemAsync_Unauthorized_ThrowsAndDoesNotMutate()
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

        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.UpdateLineItemAsync(
            lineItem.Id, "Updated", 999m, null, null, null, 0, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        var reloaded = await _dbContext.BudgetLineItems.FindAsync(lineItem.Id);
        reloaded!.Description.Should().Be("Existing");
        reloaded.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task DeleteLineItemAsync_Unauthorized_ThrowsAndDoesNotRemove()
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

        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.DeleteLineItemAsync(lineItem.Id, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _dbContext.BudgetLineItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateYearAsync_Unauthorized_Throws()
    {
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.CreateYearAsync("2027", "Budget 2027", Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _dbContext.BudgetYears.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateYearStatusAsync_Unauthorized_Throws()
    {
        var year = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = "2026",
            Name = "Budget 2026",
            Status = BudgetYearStatus.Draft
        };
        _dbContext.BudgetYears.Add(year);
        await _dbContext.SaveChangesAsync();

        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.UpdateYearStatusAsync(year.Id, BudgetYearStatus.Active, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        var reloaded = await _dbContext.BudgetYears.FindAsync(year.Id);
        reloaded!.Status.Should().Be(BudgetYearStatus.Draft);
    }

    [Fact]
    public async Task CreateGroupAsync_Unauthorized_Throws()
    {
        var year = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = "2026",
            Name = "Budget 2026"
        };
        _dbContext.BudgetYears.Add(year);
        await _dbContext.SaveChangesAsync();

        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.CreateGroupAsync(year.Id, "Gear", false, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _dbContext.BudgetGroups.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateCategoryAsync_Unauthorized_Throws()
    {
        var year = new BudgetYear { Id = Guid.NewGuid(), Year = "2026", Name = "Budget 2026" };
        var group = new BudgetGroup { Id = Guid.NewGuid(), BudgetYearId = year.Id, Name = "General" };
        _dbContext.BudgetYears.Add(year);
        _dbContext.BudgetGroups.Add(group);
        await _dbContext.SaveChangesAsync();

        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _service.CreateCategoryAsync(
            group.Id, "Supplies", 100m, ExpenditureType.OpEx, null, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _dbContext.BudgetCategories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateYearAsync_AuthorizedFinanceAdmin_Succeeds()
    {
        var year = await _service.CreateYearAsync("2027", "Budget 2027", Guid.NewGuid(), _financeAdmin);

        year.Should().NotBeNull();
        year.Year.Should().Be("2027");
        (await _dbContext.BudgetYears.CountAsync()).Should().BeGreaterThan(0);

        await _authorizationService.Received().AuthorizeAsync(
            _financeAdmin, Arg.Is<object?>(r => r == null), Arg.Any<IEnumerable<IAuthorizationRequirement>>());
    }

    [Fact]
    public async Task CreateLineItemAsync_PassesCategoryAsResource()
    {
        var category = await SeedCategoryAsync();

        await _service.CreateLineItemAsync(
            category.Id, "Valid", 100m, null, null, null, 0, Guid.NewGuid(), _financeAdmin);

        // Verify the resource passed to AuthorizeAsync is the loaded BudgetCategory
        await _authorizationService.Received().AuthorizeAsync(
            _financeAdmin,
            Arg.Is<object?>(o => o != null && ((BudgetCategory)o).Id == category.Id),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>());
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

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "finance@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
