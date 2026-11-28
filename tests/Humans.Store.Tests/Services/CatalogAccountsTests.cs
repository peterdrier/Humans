using AwesomeAssertions;
using Humans.Audit.Contracts;
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
    private readonly CatalogService _catalog;

    public CatalogAccountsTests()
    {
        _holded.GetChartAsync(Arg.Any<CancellationToken>()).Returns([
            Account(75900008, "Gas cylinders"),
            Account(75900009, "Old ice", archived: true),
            Account(70500000, "Prestaciones de servicios"),
            Account(62900001, "Gas purchases"),
            Account(62990001, "Gas team usage"),
            Account(57200000, "Bank"),
            Account(56000000, "Fianzas recibidas a corto plazo"),
        ]);
        _catalog = new CatalogService(_repo, _audit, new FakeClock(Instant.FromUtc(2026, 3, 14, 12, 0)), _holded,
            NullLogger<CatalogService>.Instance);
    }

    [HumansFact]
    public async Task Choices_UnarchivedChart_SplitIntoThreeBlocks()
    {
        var choices = await _catalog.GetAccountChoicesAsync(TestContext.Current.CancellationToken);

        choices.Income.Should().BeEquivalentTo(new[]
        {
            new AccountChoice(70500000, "Prestaciones de servicios"),
            new AccountChoice(75900008, "Gas cylinders"),
        });
        choices.Cost.Select(c => c.Number).Should().Equal(62900001);
        choices.TeamUsage.Select(c => c.Number).Should().Equal(62990001);
        choices.Income.Single(c => c.Number == 75900008).Label.Should().Be("75900008 · Gas cylinders");
    }

    [HumansFact]
    public async Task Save_AccountNotInChart_IsRefused()
    {
        var product = MakeProduct("Ice");
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var draft = CatalogService.MapProduct(product) with { HoldedRevenueAccountNum = 75900009 };

        var result = await _catalog.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("Income account 75900009 is no longer in the Holded chart");
        await _repo.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Save_AccountAlreadyHeld_AllowsOtherEdits()
    {
        var product = MakeProduct("Ice", income: 75900009);
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var draft = CatalogService.MapProduct(product) with { UnitPriceEur = 12m };

        var result = await _catalog.UpdateProductAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task BulkSave_ChangedRows_OnlyThoseWritten()
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
            StoreAuditActions.StoreProductUpdated, StoreAuditSubjectTypes.Product, gas.Id,
            Arg.Is<string>(s => s.Contains("income 75900008")), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task BulkSave_OneBadRow_WritesNothing()
    {
        var gas = MakeProduct("Gas");
        var ice = MakeProduct("Ice");
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([gas, ice]);

        var result = await _catalog.SaveAccountsAsync(2026, [
            new ProductAccounts(gas.Id, 75900008, null, null),
            new ProductAccounts(ice.Id, null, 62990001, null),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be("Ice: Cost account 62990001 is no longer in the Holded chart. Pick another.");
        await _repo.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Settings_StripeTreasury_SavedWithAudit()
    {
        var actor = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var saved = await _catalog.SaveSettingsAsync(new StoreSettingsDto(" 6a22e0f6e5e9f791670d311a ", 400m), actor, ct);

        saved.Succeeded.Should().BeTrue();
        await _repo.Received(1).SaveSettingsAsync(
            Arg.Is<StoreSettings>(s => s.StripeGatewayTreasuryId == "6a22e0f6e5e9f791670d311a" && s.SimplifiedInvoiceThresholdEur == 400m),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(StoreAuditActions.StoreSettingsUpdated, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("6a22e0f6e5e9f791670d311a")), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    private static HoldedChartAccountInfo Account(int number, string name, bool archived = false) =>
        new(number, name, null, archived, 0m);

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
