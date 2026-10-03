using System.Globalization;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Tickets.Models;
using Humans.Tickets.Services.Dtos;
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

    private static async Task<string> RenderAsync(string page, object model)
    {
        using var culture = new CultureScope("en");
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
        var result = engine.GetView(null, $"/Views/Ticket/{page}.cshtml", isMainPage: false);
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
