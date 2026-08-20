using Humans.Camps.Data;
using Humans.Camps.Domain;
using Humans.AuditLog.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

public class StoreControllerTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 30000)]
    public async Task Anonymous_GET_Store_redirects_to_login()
    {
        var resp = await Client.GetAsync("/Store", Xunit.TestContext.Current.CancellationToken);
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [HumansFact(Timeout = 30000)]
    public async Task LoggedIn_camp_lead_can_GET_Store()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("barrio-1-lead"));
        var year = await SeedActiveProductAsync("Test product");

        var resp = await Client.GetAsync("/Store", Xunit.TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        body.Should().Contain("Test product");
        body.Should().Contain(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [HumansFact(Timeout = 30000)]
    public async Task Volunteer_GET_Store_returns_200_with_catalog_only()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SeedActiveProductAsync("Volunteer-visible product");

        var resp = await Client.GetAsync("/Store", Xunit.TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
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
            BuildForm(("label", "should-not-create")), Xunit.TestContext.Current.CancellationToken);

        // Anti-forgery / forbid produce non-2xx outcomes; assert it isn't a happy redirect to /Store/Order/{id}.
        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(300);
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            (resp.Headers.Location?.PathAndQuery ?? string.Empty)
                .Should().NotContain("/Store/Order/");
        }
    }

    /// <summary>
    /// The order page's price-change panel renders through
    /// <c>&lt;vc:audit-log layout="table" since="..."&gt;</c> since the read moved out of
    /// <c>StoreService</c> (nobodies-collective/Humans#816). Asserting a seeded marker is the
    /// only probe that catches an unbound tag helper: it ships as inert literal markup with a
    /// green build and no runtime error.
    /// </summary>
    [HumansFact(Timeout = 60000)]
    public async Task Order_page_renders_price_changes_through_the_audit_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var leadId = await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("barrio-1-lead"));
        await SeedActiveProductAsync("Price-probe product");
        var seasonId = await GetBarrioOneCampSeasonIdAsync();
        seasonId.Should().NotBe(Guid.Empty);

        var orderCreatedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(1));
        Guid orderId;
        Guid productId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var storeDb = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var product = await storeDb.Products.FirstAsync(p => p.Name == "Price-probe product", ct);
            productId = product.Id;
            var order = new Order
            {
                Id = Guid.NewGuid(),
                CampSeasonId = seasonId,
                Year = product.Year,
                State = OrderState.Open,
                CreatedAt = orderCreatedAt,
                UpdatedAt = orderCreatedAt
            };
            order.Lines.Add(new OrderLine
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ProductId = productId,
                Qty = 1,
                UnitPriceSnapshot = product.UnitPriceEur,
                VatRateSnapshot = product.VatRatePercent,
                AddedAt = orderCreatedAt,
                AddedByUserId = leadId
            });
            storeDb.Orders.Add(order);
            await storeDb.SaveChangesAsync(ct);
            orderId = order.Id;
        }

        // Written now, so it lands after the order's CreatedAt and survives the since filter.
        var marker = $"price-change-probe-{Guid.NewGuid():N}";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            await auditLog.LogAsync(
                AuditAction.StoreProductPriceChanged,
                entityType: "StoreProduct",
                entityId: productId,
                description: marker,
                actorUserId: leadId);
        }

        var url = $"/Store/Order/{orderId}";
        var response = await Client.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain(marker,
            $"GET {url}: the seeded price-change row must reach the panel");
        html.Should().NotContain("<vc:audit-log",
            $"GET {url}: the widget must bind, not ship as literal markup");
    }

    private async Task<int> SeedActiveProductAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampsDbContext>();
        var storeDb = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var year = (await db.CampSettings.FirstAsync(Xunit.TestContext.Current.CancellationToken)).PublicYear;

        if (await storeDb.Products.AnyAsync(p => p.Year == year && p.Name == name, Xunit.TestContext.Current.CancellationToken))
            return year;

        storeDb.Products.Add(new Product
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
        await storeDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return year;
    }

    private async Task<Guid> GetBarrioOneCampSeasonIdAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampsDbContext>();
        var year = (await db.CampSettings.FirstAsync(Xunit.TestContext.Current.CancellationToken)).PublicYear;
        var season = await db.Set<CampSeason>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Year == year && s.Camp.Slug == "barrio-1", Xunit.TestContext.Current.CancellationToken);
        return season?.Id ?? Guid.Empty;
    }

    private static FormUrlEncodedContent BuildForm(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(f =>
            new KeyValuePair<string, string>(f.Key, f.Value)));
    }
}
