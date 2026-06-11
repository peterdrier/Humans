using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

public class StoreAdminControllerTests(HumansWebApplicationFactory factory) : IntegrationTestBase(factory)
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

        // GET edit form to seed antiforgery token + cookie.
        var editResp = await Client.GetAsync("/Store/Admin/Catalog/Edit", Xunit.TestContext.Current.CancellationToken);
        editResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var editBody = await editResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        var token = ExtractAntiForgeryToken(editBody);
        token.Should().NotBeNullOrEmpty();

        // POST Save (no Id) → product created
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

        // Resolve the new product's id.
        var createdId = await GetProductIdByNameAsync(uniqueName);
        createdId.Should().NotBe(Guid.Empty);

        // GET Catalog → product visible
        var catalogResp = await Client.GetAsync("/Store/Admin/Catalog", Xunit.TestContext.Current.CancellationToken);
        catalogResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await catalogResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken)).Should().Contain(uniqueName);

        // POST Save (with Id, modified fields) → product updated
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

        // POST Deactivate/{id}
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

    private async Task<int> GetActiveYearAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        return (await db.CampSettings.FirstAsync(Xunit.TestContext.Current.CancellationToken)).PublicYear;
    }

    private async Task<Guid> GetProductIdByNameAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var p = await db.StoreProducts.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, Xunit.TestContext.Current.CancellationToken);
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
