using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Store.Tests.Services;

public class CatalogAccountsTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly StoreCatalog _catalog;

    public CatalogAccountsTests()
    {
        _holded.GetChartAsync(Arg.Any<CancellationToken>()).Returns([
            Account(75900008, "Gas cylinders"),
            Account(75900009, "Old ice", archived: true),
            Account(70500000, "Prestaciones de servicios"),
            Account(62900001, "Gas purchases"),
            Account(62990001, "Gas team usage"),
            Account(57200000, "Bank"),
        ]);
        _catalog = new StoreCatalog(_repo, _audit, _holded,
            new FakeClock(Instant.FromUtc(2026, 3, 14, 12, 0)), NullLogger<Service>.Instance);
    }

    [HumansFact]
    public async Task Choices_split_the_unarchived_chart_into_the_three_blocks()
    {
        var choices = await _catalog.GetAccountChoicesAsync(TestContext.Current.CancellationToken);

        choices.Income.Select(c => c.Number).Should().Equal(70500000, 75900008);
        choices.Cost.Select(c => c.Number).Should().Equal(62900001);
        choices.TeamUsage.Select(c => c.Number).Should().Equal(62990001);
        choices.Income[1].Label.Should().Be("75900008 · Gas cylinders");
    }

    [HumansFact]
    public async Task Saving_a_product_with_an_account_no_longer_in_the_chart_is_refused()
    {
        var product = MakeProduct("Ice");
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var draft = StoreCatalog.MapProduct(product) with { HoldedRevenueAccountNum = 75900009 };

        var result = await _catalog.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("Income account 75900009 is no longer in the Holded chart");
        await _repo.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task An_account_the_product_already_holds_does_not_block_other_edits()
    {
        var product = MakeProduct("Ice", income: 75900009);
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var draft = StoreCatalog.MapProduct(product) with { UnitPriceEur = 12m };

        var result = await _catalog.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Bulk_save_writes_and_audits_changed_rows_only()
    {
        var gas = MakeProduct("Gas");
        var ice = MakeProduct("Ice", income: 75900008);
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([gas, ice]);

        var result = await _catalog.SaveAccountsAsync(2026, [
            new ProductAccounts(gas.Id, 75900008, 62900001, 62990001),
            new ProductAccounts(ice.Id, 75900008, null, null),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        await _repo.Received(1).UpdateProductAsync(
            Arg.Is<Product>(p => p.Id == gas.Id && p.HoldedRevenueAccountNum == 75900008
                && p.HoldedExpenseAccountNum == 62900001 && p.HoldedTransferAccountNum == 62990001),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateProductAsync(Arg.Is<Product>(p => p.Id == ice.Id), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            StoreAuditActions.StoreProductUpdated, AuditEntityTypes.Product, gas.Id,
            Arg.Is<string>(s => s.Contains("income 75900008")), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task Bulk_save_with_one_bad_row_writes_nothing()
    {
        var gas = MakeProduct("Gas");
        var ice = MakeProduct("Ice");
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([gas, ice]);

        var result = await _catalog.SaveAccountsAsync(2026, [
            new ProductAccounts(gas.Id, 75900008, null, null),
            new ProductAccounts(ice.Id, null, 62990001, null),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be("Ice: Cost account 62990001 is no longer in the Holded chart (removed or archived). Pick another.");
        await _repo.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    private static HoldedChartAccountInfo Account(int number, string name, bool archived = false) =>
        new(number, name, null, null, archived, 0m);

    private static Product MakeProduct(string name, int? income = null) => new()
    {
        Id = Guid.NewGuid(),
        Year = 2026,
        Name = name,
        UnitPriceEur = 10m,
        VatRatePercent = 21m,
        OrderableUntil = new LocalDate(2026, 8, 1),
        IsActive = true,
        HoldedRevenueAccountNum = income,
    };
}
