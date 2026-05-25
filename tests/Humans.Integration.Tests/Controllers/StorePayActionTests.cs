using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Integration tests for POST /Store/Order/{id}/Pay. Stripe's HTTP layer is replaced
/// by the factory's <c>StripeServiceStub</c> — no real network calls.
/// </summary>
public class StorePayActionTests(HumansWebApplicationFactory factory) : IntegrationTestBase(factory)
{
    [HumansFact(Timeout = 30000)]
    public async Task Pay_with_valid_amount_redirects_to_Stripe_session_url()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("barrio-1-lead"));
        var (orderId, balance) = await SeedOrderWithLineAsync();

        const string sessionUrl = "https://checkout.stripe.com/c/pay/cs_test_pay_redirect";
        Factory.StripeServiceStub.IsStoreCheckoutConfigured.Returns(true);
        Factory.StripeServiceStub
            .CreateCheckoutSessionAsync(
                Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(sessionUrl);

        var (token, cookie) = await GetAntiforgeryAsync($"/Store/Order/{orderId}");

        var resp = await PostFormWithAntiforgeryAsync(
            $"/Store/Order/{orderId}/Pay",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amountEur"] = balance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            });

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Be(sessionUrl);

        await Factory.StripeServiceStub.Received(1).CreateCheckoutSessionAsync(
            orderId, balance,
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact(Timeout = 30000)]
    public async Task Pay_with_amount_over_balance_redirects_back_to_order_without_calling_Stripe()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("barrio-1-lead"));
        var (orderId, balance) = await SeedOrderWithLineAsync();

        Factory.StripeServiceStub.ClearReceivedCalls();
        Factory.StripeServiceStub.IsStoreCheckoutConfigured.Returns(true);

        var (token, cookie) = await GetAntiforgeryAsync($"/Store/Order/{orderId}");

        var overpay = (balance + 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var resp = await PostFormWithAntiforgeryAsync(
            $"/Store/Order/{orderId}/Pay",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["amountEur"] = overpay });

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Contain($"/Store/Order/{orderId}");

        await Factory.StripeServiceStub.DidNotReceive().CreateCheckoutSessionAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<(Guid OrderId, decimal BalanceEur)> SeedOrderWithLineAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var year = (await db.CampSettings.FirstAsync()).PublicYear;
        var seasonId = await db.Set<CampSeason>().AsNoTracking()
            .Where(s => s.Camp.Slug == "barrio-1")
            .Select(s => s.Id).FirstAsync();

        var product = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = "Pay test product",
            Description = "Used by Pay action integration tests",
            UnitPriceEur = 25m,
            VatRatePercent = 21m,
            DepositAmountEur = null,
            OrderableUntil = new LocalDate(year, 12, 31),
            IsActive = true,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
        db.StoreProducts.Add(product);

        var orderId = Guid.NewGuid();
        var now = SystemClock.Instance.GetCurrentInstant();
        db.StoreOrders.Add(new StoreOrder
        {
            Id = orderId,
            CampSeasonId = seasonId,
            State = Domain.Enums.StoreOrderState.Open,
            Lines = new List<StoreOrderLine>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    ProductId = product.Id,
                    Qty = 1,
                    UnitPriceSnapshot = product.UnitPriceEur,
                    VatRateSnapshot = product.VatRatePercent,
                    DepositAmountSnapshot = null,
                    AddedAt = now,
                    AddedByUserId = Guid.NewGuid(),
                },
            },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Subtotal 25 + VAT 21% = 30.25 EUR balance.
        return (orderId, 30.25m);
    }

    private async Task<(string FormToken, string Cookie)> GetAntiforgeryAsync(string url)
    {
        var resp = await Client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET {url} must render so we can harvest its antiforgery token (got {(int)resp.StatusCode}).");

        var html = await resp.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html,
            @"name=""__RequestVerificationToken""[^>]*value=""(?<v>[^""]+)""",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
        if (!match.Success)
            throw new InvalidOperationException($"No antiforgery token in {url}.");
        var formToken = match.Groups["v"].Value;

        if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            throw new InvalidOperationException($"No Set-Cookie on GET {url}.");
        var antiforgeryCookie = setCookies
            .Select(h => h.Split(';', 2)[0])
            .FirstOrDefault(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No antiforgery cookie on GET.");
        return (formToken, antiforgeryCookie);
    }

    private async Task<HttpResponseMessage> PostFormWithAntiforgeryAsync(
        string url, string formToken, string antiforgeryCookie, IDictionary<string, string> fields)
    {
        var withToken = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = formToken,
        };
        using var content = new FormUrlEncodedContent(withToken);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        return await Client.SendAsync(req);
    }
}
