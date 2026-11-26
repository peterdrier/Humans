using Humans.AuditLog.Data;
using Humans.Camps.Data;
using Humans.Store.Data;
using Humans.Users.Contracts;
using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

public class StoreAdminControllerTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60000)]
    public async Task Volunteer_GET_admin_catalog_returns_403_or_redirect()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var resp = await Client.GetAsync("/Store/Admin/Catalog", Xunit.TestContext.Current.CancellationToken);

        ((int)resp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Forbidden,
            (int)HttpStatusCode.Found,
            (int)HttpStatusCode.Redirect);
    }

    [HumansFact(Timeout = 60000)]
    public async Task Full_create_edit_deactivate_cycle_works_for_StoreAdmin()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("store-admin"));
        var year = await GetActiveYearAsync();

        var editResp = await Client.GetAsync("/Store/Admin/Catalog/Edit", Xunit.TestContext.Current.CancellationToken);
        editResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var editBody = await editResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        var token = ExtractAntiForgeryToken(editBody);
        token.Should().NotBeNullOrEmpty();

        var uniqueName = $"Integration test product {Guid.NewGuid():N}";
        var createResp = await Client.PostAsync("/Store/Admin/Catalog/Save", BuildForm(
            ("__RequestVerificationToken", token),
            ("Year", year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Name", uniqueName),
            ("Description", "Created by integration test"),
            ("UnitPriceEur", "42.50"),
            ("VatRatePercent", "21"),
            ("DepositAmountEur", ""),
            ("OrderableUntil", $"{year}-12-31"),
            ("IsActive", "true")), Xunit.TestContext.Current.CancellationToken);
        ((int)createResp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);

        var createdId = await GetProductIdByNameAsync(uniqueName);
        createdId.Should().NotBe(Guid.Empty);

        var catalogResp = await Client.GetAsync("/Store/Admin/Catalog", Xunit.TestContext.Current.CancellationToken);
        catalogResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await catalogResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken)).Should().Contain(uniqueName);

        var editIdResp = await Client.GetAsync($"/Store/Admin/Catalog/Edit/{createdId}", Xunit.TestContext.Current.CancellationToken);
        editIdResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var editIdBody = await editIdResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        var editToken = ExtractAntiForgeryToken(editIdBody);

        var renamed = $"{uniqueName} (renamed)";
        var updateResp = await Client.PostAsync("/Store/Admin/Catalog/Save", BuildForm(
            ("__RequestVerificationToken", editToken!),
            ("Id", createdId.ToString()),
            ("Year", year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Name", renamed),
            ("Description", "Edited"),
            ("UnitPriceEur", "55.00"),
            ("VatRatePercent", "10"),
            ("DepositAmountEur", "20.00"),
            ("OrderableUntil", $"{year}-11-30"),
            ("IsActive", "true")), Xunit.TestContext.Current.CancellationToken);
        ((int)updateResp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);

        var catalogAfterEdit = await Client.GetAsync("/Store/Admin/Catalog", Xunit.TestContext.Current.CancellationToken);
        (await catalogAfterEdit.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken)).Should().Contain(renamed);

        var deactivateResp = await Client.PostAsync(
            $"/Store/Admin/Catalog/Deactivate/{createdId}",
            BuildForm(("__RequestVerificationToken", editToken!)), Xunit.TestContext.Current.CancellationToken);
        ((int)deactivateResp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);

        // Admin Catalog still shows it (with Inactive badge), camp-lead /Store does not.
        var adminCatalogAfter = await Client.GetAsync("/Store/Admin/Catalog", Xunit.TestContext.Current.CancellationToken);
        var adminBody = await adminCatalogAfter.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        adminBody.Should().Contain(renamed);
        adminBody.Should().Contain("Inactive");

        var storeIndex = await Client.GetAsync("/Store", Xunit.TestContext.Current.CancellationToken);
        storeIndex.StatusCode.Should().Be(HttpStatusCode.OK);
        (await storeIndex.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken)).Should().NotContain(renamed);
    }

    /// <summary>
    /// A Spanish admin's price edit end to end: the number input, the posted price and the audit text stay
    /// dot-decimal (peterdrier/Humans#2581 guard).
    /// </summary>
    [HumansFact(Timeout = 60000)]
    public async Task CatalogSave_SpanishAdmin_StaysDotDecimal()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("store-admin"));
        using (var setup = Factory.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<IUserWrite>().SetPreferredLanguageAsync(adminId, "es", ct);
        var year = (await GetActiveYearAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var name = $"Culture test product {Guid.NewGuid():N}";
        var newToken = ExtractAntiForgeryToken(
            await Client.GetStringAsync("/Store/Admin/Catalog/Edit", ct));
        await Client.PostAsync("/Store/Admin/Catalog/Save", BuildForm(
            ("__RequestVerificationToken", newToken!), ("Year", year), ("Name", name), ("UnitPriceEur", "42.50"),
            ("VatRatePercent", "21"), ("OrderableUntil", $"{year}-12-31"), ("IsActive", "true")), ct);
        var id = await GetProductIdByNameAsync(name);

        var editBody = await Client.GetStringAsync($"/Store/Admin/Catalog/Edit/{id}", ct);
        await Client.PostAsync("/Store/Admin/Catalog/Save", BuildForm(
            ("__RequestVerificationToken", ExtractAntiForgeryToken(editBody)!), ("Id", id.ToString()), ("Year", year),
            ("Name", name), ("UnitPriceEur", "55.25"), ("VatRatePercent", "21"), ("OrderableUntil", $"{year}-12-31"),
            ("IsActive", "true")), ct);

        editBody.Should().Contain("<html lang=\"es\"");
        editBody.Should().MatchRegex("id=\"priceExVat\"[^>]*value=\"42.50\"|value=\"42.50\"[^>]*id=\"priceExVat\"");
        using var scope = Factory.Services.CreateScope();
        var product = await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Products.AsNoTracking()
            .SingleAsync(p => p.Id == id, ct);
        product.UnitPriceEur.Should().Be(55.25m);
        var audit = await scope.ServiceProvider.GetRequiredService<AuditLogDbContext>().AuditLogEntries.AsNoTracking()
            .SingleAsync(a => a.EntityId == id && a.Action == "StoreProductPriceChanged", ct);
        audit.Description.Should().EndWith("changed from 42.50 to 55.25");
    }

    private async Task<int> GetActiveYearAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampsDbContext>();
        return (await db.CampSettings.FirstAsync(Xunit.TestContext.Current.CancellationToken)).PublicYear;
    }

    private async Task<Guid> GetProductIdByNameAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, Xunit.TestContext.Current.CancellationToken);
        return p?.Id ?? Guid.Empty;
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }

    private static FormUrlEncodedContent BuildForm(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(f =>
            new KeyValuePair<string, string>(f.Key, f.Value)));
    }
}
