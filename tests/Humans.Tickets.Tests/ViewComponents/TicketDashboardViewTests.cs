using System.Globalization;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Tickets.Models;
using Humans.Tickets.Services.Dtos;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NodaTime;
using Xunit;

namespace Humans.Tickets.Tests.ViewComponents;

public class TicketDashboardViewTests
{
    [HumansTheory]
    [InlineData("Orders", "buyer@example.com")]
    [InlineData("Attendees", "attendee@example.com")]
    [InlineData("Codes", "EXAMPLE-CODE")]
    [InlineData("WhoHasntBought", "human@example.com")]
    [InlineData("SalesAggregates", "2026-07")]
    public async Task DashboardPages_RenderServiceRows(string page, string expected)
    {
        object model = page switch
        {
            "Orders" => new TicketOrdersViewModel { Orders = [new OrderRow { BuyerEmail = expected }] },
            "Attendees" => new TicketAttendeesViewModel { Attendees = [new AttendeeRow { AttendeeEmail = expected }] },
            "Codes" => new TicketCodeTrackingViewModel
            {
                Campaigns = [new CampaignCodeSummaryDto { CampaignTitle = "Example campaign" }],
                Codes = [new CodeDetailDto { Code = expected }],
            },
            "WhoHasntBought" => new WhoHasntBoughtViewModel { Humans = [new WhoHasntBoughtRowDto { Email = expected }] },
            "SalesAggregates" => new TicketSalesAggregatesViewModel
            {
                WeeklySales = [new WeeklySalesAggregate { WeekLabel = "Example week" }],
                QuarterlySales = [new QuarterlySalesAggregate { QuarterLabel = "Example quarter" }],
                MonthlySales = [new MonthlySalesAggregate { MonthLabel = expected }],
                ByTicketType = [new TicketTypeSalesAggregate { TicketTypeName = "Example ticket" }],
                ByDiscountCampaign = [new DiscountCampaignAggregate { CampaignTitle = "Example campaign" }],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        var html = await RenderAsync(page, model);
        Assert.Contains(expected, html);
        if (string.Equals(page, "SalesAggregates", StringComparison.Ordinal))
        {
            Assert.Contains("Example week", html);
            Assert.Contains("Example quarter", html);
            Assert.Contains("Example ticket", html);
            Assert.Contains("Example campaign", html);
        }
    }

    [HumansTheory]
    [InlineData(315, false)]
    [InlineData(400, true)]
    public async Task Attendees_RenderVipSplitOnlyAboveThreshold(int price, bool vip)
    {
        var model = new TicketAttendeesViewModel { Attendees = [new AttendeeRow { Price = price }] };
        var html = await RenderAsync("Attendees", model);
        Assert.Equal(vip, html.Contains("title=\"VIP ticket\"", StringComparison.Ordinal));
        Assert.Equal(vip, html.Contains("title=\"Taxable portion\"", StringComparison.Ordinal));
        if (vip)
        {
            Assert.Contains("315.00", html);
            Assert.Contains("85.00", html);
            Assert.DoesNotContain(").ToString", html);
        }
    }

    [HumansTheory]
    [InlineData("en", "1,234.50 USD")]
    [InlineData("es", "1.234,50 USD")]
    [InlineData("de", "1.234,50 USD")]
    [InlineData("it", "1.234,50 USD")]
    [InlineData("fr", "1\u202f234,50 USD")]
    [InlineData("ca", "1.234,50 USD")]
    public async Task GuestOrders_RenderAmountInUiCultureAndKeepCurrencyCode(string culture, string expected)
    {
        var orders = new List<UserTicketOrderSummary>
        {
            new("Buyer", Instant.FromUtc(2026, 7, 1, 12, 0), 2, 1234.5m, "USD"),
        };
        var html = await RenderAsync("GuestTicketOrders", orders, culture,
            "/Views/Shared/Components/GuestTicketOrders/Default.cshtml");

        Assert.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(expected), html);
        Assert.DoesNotContain("€", html);
    }

    [HumansTheory]
    [InlineData("en", "1,234", "1,234.50", "315.00", "919.50", "12.5%")]
    [InlineData("es", "1.234", "1.234,50", "315,00", "919,50", "12,5%")]
    [InlineData("de", "1.234", "1.234,50", "315,00", "919,50", "12,5%")]
    [InlineData("it", "1.234", "1.234,50", "315,00", "919,50", "12,5%")]
    [InlineData("fr", "1\u202f234", "1\u202f234,50", "315,00", "919,50", "12,5%")]
    [InlineData("ca", "1.234", "1.234,50", "315,00", "919,50", "12,5%")]
    public async Task DashboardNumbers_UseUiCultureAcrossCardsRowsAndTotals(
        string language, string count, string amount, string taxable, string vip, string rate)
    {
        var index = await RenderAsync("Index", new TicketDashboardViewModel
        {
            IsConfigured = true, TicketsSold = 1234, TicketsRemaining = 1234,
            TotalCapacity = 1234, BreakEvenTarget = 1234, Revenue = 1234, NetRevenue = 1234,
            AveragePrice = 1234.5m, TotalStripeFees = 1234, TotalApplicationFees = 1234,
            FeesByPaymentMethod = [new PaymentMethodFeeBreakdown
            {
                TotalAmount = 1234, TotalStripeFees = 1234.5m, TotalApplicationFees = 1234.5m,
                EffectiveRate = 12.5m,
            }],
            RecentOrders = [new TicketOrderSummary { Amount = 1234 }],
            DailySales = [new DailySalesPoint { Date = "2026-07-01", RollingAverage = 12.5m }],
        }, language);
        Assert.True(index.Split(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(count), StringSplitOptions.None).Length >= 8);
        Assert.True(index.Split(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(amount), StringSplitOptions.None).Length >= 4);
        Assert.Contains(rate, index);
        Assert.Contains("\"rollingAverage\":12.5", index);

        var orders = await RenderAsync("Orders", new TicketOrdersViewModel
        {
            Orders = [new OrderRow
            {
                TotalAmount = 1234.5m, DiscountAmount = 1234.5m, DonationAmount = 1234.5m,
                VatAmount = 1234.5m, StripeFee = 1234.5m, ApplicationFee = 1234.5m,
            }],
        }, language);
        Assert.Equal(7, orders.Split(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(amount), StringSplitOptions.None).Length);

        var attendees = await RenderAsync("Attendees", new TicketAttendeesViewModel
        {
            Attendees = [new AttendeeRow { Price = 1234.5m }],
        }, language);
        Assert.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(amount), attendees);
        Assert.Contains(taxable, attendees);
        Assert.Contains(vip, attendees);

        var aggregates = await RenderAsync("SalesAggregates", new TicketSalesAggregatesViewModel
        {
            WeeklySales = [new WeeklySalesAggregate
            {
                OrderCount = 1234, TicketsSold = 1234, GrossRevenue = 1234.5m,
                Donations = 1234.5m, VatAmount = 1234.5m, VipDonations = 1234.5m,
            }],
            QuarterlySales = [new QuarterlySalesAggregate
            {
                OrderCount = 1234, TicketsSold = 1234, GrossRevenue = 1234.5m,
                Donations = 1234.5m, VatAmount = 1234.5m, VipDonations = 1234.5m,
            }],
            MonthlySales = [new MonthlySalesAggregate
            {
                OrderCount = 1234, TicketsSold = 1234, GrossRevenue = 1234.5m,
                Donations = 1234.5m, VatAmount = 1234.5m, VipDonations = 1234.5m,
                StripeFees = 1234.5m, ApplicationFees = 1234.5m, RefundedGross = 1234.5m,
                TicketIncomeInclVat = 1234.5m,
            }],
            ByTicketType = [new TicketTypeSalesAggregate { Price = 1234.5m, TicketsSold = 1234, FaceValue = 1234.5m }],
            ByDiscountCampaign = [new DiscountCampaignAggregate
            {
                CodesGranted = 1234, CodesUsed = 1234, AverageDiscount = 1234.5m, TotalDiscount = 1234.5m,
            }],
        }, language);
        Assert.True(aggregates.Split(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(amount), StringSplitOptions.None).Length >= 36);
    }

    private static async Task<string> RenderAsync(string page, object model, string cultureName = "en", string? viewPath = null)
    {
        using var culture = new CultureScope(cultureName);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TicketDashboardViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(TicketsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        builder.Services.AddSingleton(Substitute.For<IUserServiceRead>());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        builder.Services.AddSingleton(authorization);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var engine = services.GetRequiredService<IRazorViewEngine>();
        var result = engine.GetView(null, viewPath ?? $"/Views/Ticket/{page}.cshtml", isMainPage: false);
        Assert.True(result.Success, string.Join(", ", result.SearchedLocations ?? []));
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Path = $"/Tickets/{page}";
        var route = new RouteData();
        route.Values["controller"] = "Ticket";
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View.RenderAsync(context);
        return writer.ToString();
    }
}
