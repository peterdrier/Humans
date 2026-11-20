using System.Text.Encodings.Web;
using AngleSharp.Html.Parser;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Builder;
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

namespace Humans.Camps.Tests;

public class CampAdminViewTests
{
    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task DescriptionPreviews_PreserveWholeSurrogatePairs(bool withdrawn, bool fits)
    {
        var prefix = new string('a', fits ? 98 : 99);
        var camp = new CampCardViewModel
        {
            Id = Guid.NewGuid(),
            SeasonId = Guid.NewGuid(),
            Name = "Camp",
            Slug = "camp",
            BlurbShort = prefix + "😀tail",
        };
        var model = new CampAdminViewModel();
        if (withdrawn) model.WithdrawnCamps.Add(camp);
        else model.PendingCamps.Add(camp);
        var html = await RenderAsync("Index", model);

        html.Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "..."));
        camp.BlurbShort.Should().Be(prefix + "😀tail");
    }

    [HumansFact]
    public async Task SummaryFooterAndEarlyEntryCard_ShareActiveEventTotals()
    {
        var model = new CampAdminViewModel
        {
            AllCampSummaries =
            [
                new() { Name = "A", Slug = "a", MemberCount = 3, JoinedMemberCount = 2, EeGrantedCount = 3, EeSlotCount = 4 },
                new() { Name = "B", Slug = "b", MemberCount = 5, JoinedMemberCount = 4, EeGrantedCount = 8, EeSlotCount = 6 },
            ]
        };

        var html = await RenderAsync("Index", model);
        var document = await new HtmlParser().ParseDocumentAsync(html, Xunit.TestContext.Current.CancellationToken);
        var footer = document.QuerySelectorAll("tfoot td").Select(cell => cell.TextContent.Trim()).ToList();

        footer.Should().Equal("Totals", "", "8", "6", "", "", "", "11 / 10", "");
        document.Body!.TextContent.Should().Contain("11 granted").And.Contain("10 configured slots").And.Contain("over cap");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task MemberGrantButtons_UseCapacityIncludingConsumedGrants(bool hasCapacity)
    {
        var model = new CampEditViewModel
        {
            EeSlotCount = 2,
            EeGrantedCount = 1,
            HasEarlyEntryCapacity = hasCapacity,
            ActiveMembers =
            [
                new() { CampMemberId = Guid.NewGuid(), HasEarlyEntry = false },
                new() { CampMemberId = Guid.NewGuid(), HasEarlyEntry = true },
            ]
        };
        var html = await RenderAsync("Members", model, "Camp");
        var document = await new HtmlParser().ParseDocumentAsync(html, Xunit.TestContext.Current.CancellationToken);
        var grant = document.QuerySelector("input[name=granted][value=true]")!.ParentElement!.QuerySelector("button")!;
        var revoke = document.QuerySelector("input[name=granted][value=false]")!.ParentElement!.QuerySelector("button")!;

        grant.HasAttribute("disabled").Should().Be(!hasCapacity);
        revoke.HasAttribute("disabled").Should().BeFalse();
    }

    private static async Task<string> RenderAsync(string page, object model, string controller = "CampAdmin")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CampAdminViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddSingleton(Substitute.For<IUserService>());
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(CampsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, $"/Views/{controller}/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = controller;
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        return writer.ToString();
    }
}
