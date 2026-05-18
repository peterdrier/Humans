using System.Net;
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

public class StoreControllerTests(HumansWebApplicationFactory factory) : IntegrationTestBase(factory)
{
    [HumansFact(Timeout = 30000)]
    public async Task Anonymous_GET_Store_redirects_to_login()
    {
        var resp = await Client.GetAsync("/Store");
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [HumansFact(Timeout = 30000)]
    public async Task LoggedIn_camp_lead_can_GET_Store()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("barrio-1-lead"));
        var year = await SeedActiveProductAsync("Test product");

        var resp = await Client.GetAsync("/Store");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Test product");
        body.Should().Contain(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [HumansFact(Timeout = 30000)]
    public async Task Volunteer_GET_Store_returns_200_with_catalog_only()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SeedActiveProductAsync("Volunteer-visible product");

        var resp = await Client.GetAsync("/Store");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Volunteer-visible product");
    }

    [HumansFact(Timeout = 30000)]
    public async Task Volunteer_cannot_create_order_against_someone_elses_camp_season()
    {
        // Make sure barrio-1's camp + season are seeded (lead persona seeds them).
        using (var leadClient = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        }))
        {
            await Factory.SignInAsFullyOnboardedAsync(leadClient, new DevPersona("barrio-1-lead"));
        }

        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var seasonId = await GetBarrioOneCampSeasonIdAsync();
        seasonId.Should().NotBe(Guid.Empty);

        var resp = await Client.PostAsync(
            $"/Store/Order/Create/{seasonId}",
            BuildForm(("label", "should-not-create")));

        // Anti-forgery / forbid produce non-2xx outcomes; assert it isn't a happy redirect to /Store/Order/{id}.
        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(300);
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            (resp.Headers.Location?.PathAndQuery ?? string.Empty)
                .Should().NotContain("/Store/Order/");
        }
    }

    private async Task<int> SeedActiveProductAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var year = (await db.CampSettings.FirstAsync()).PublicYear;

        if (await db.StoreProducts.AnyAsync(p => p.Year == year && p.Name == name))
            return year;

        db.StoreProducts.Add(new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = name,
            Description = "Description for integration test",
            UnitPriceEur = 25m,
            VatRatePercent = 21m,
            DepositAmountEur = null,
            OrderableUntil = new LocalDate(year, 12, 31),
            IsActive = true,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
        });
        await db.SaveChangesAsync();
        return year;
    }

    private async Task<Guid> GetBarrioOneCampSeasonIdAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var year = (await db.CampSettings.FirstAsync()).PublicYear;
        var season = await db.Set<CampSeason>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Year == year && s.Camp.Slug == "barrio-1");
        return season?.Id ?? Guid.Empty;
    }

    private static FormUrlEncodedContent BuildForm(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(f =>
            new KeyValuePair<string, string>(f.Key, f.Value)));
    }
}
