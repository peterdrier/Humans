using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Feedback.Contracts;
using Humans.Feedback.Models;
using Humans.Feedback.Domain;
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

namespace Humans.Feedback.Tests.Services;

public class FeedbackListViewTests
{
    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task AssigneeBadges_PreserveWholeSurrogatePairs(bool team, bool fits)
    {
        var prefix = new string('a', fits ? 10 : 11);
        var name = prefix + "😀tail";
        var report = new FeedbackListItemViewModel
        {
            Id = Guid.NewGuid(),
            Category = FeedbackCategory.Bug,
            Status = FeedbackStatus.Open,
            PageUrl = "/",
            CreatedAt = new DateTime(2026, 7, 1),
            AssignedToName = team ? null : name,
            AssignedToTeamName = team ? name : null,
        };
        var html = await RenderAsync("Index", new FeedbackPageViewModel { Reports = [report] });

        html.Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "..."));
        html.Should().Contain(HtmlEncoder.Default.Encode(name), "the tooltip keeps the full name");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task UrlPreviews_PreserveWholeSurrogatePairs(bool detail, bool fits)
    {
        var limit = detail ? 40 : 30;
        var prefix = "/" + new string('a', limit - (fits ? 3 : 2));
        var url = prefix + "😀tail";
        object model = detail
            ? new FeedbackDetailViewModel { PageUrl = url, CreatedAt = new DateTime(2026, 7, 1) }
            : new FeedbackPageViewModel { Reports = [new FeedbackListItemViewModel { PageUrl = url, CreatedAt = new DateTime(2026, 7, 1) }] };
        var html = await RenderAsync(detail ? "_Detail" : "Index", model);

        html.Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "..."));
        html.Should().NotContain("&#xFFFD;");
    }

    [HumansFact]
    public async Task ReporterOptions_render_encoded_labels_and_preserve_selection()
    {
        var id = Guid.NewGuid().ToString();
        var html = await RenderAsync("Index", new FeedbackPageViewModel
        {
            Reporters = [new SelectListItem("Alice & Bob (2)", id, selected: true)]
        });

        html.Should().Contain($"value=\"{id}\"");
        html.Should().Contain("Alice &amp; Bob (2)");
        html.Should().Contain("selected=\"selected\"");
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FeedbackListViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(Substitute.For<Humans.Users.Contracts.IUserServiceRead>());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(FeedbackResource).Assembly)
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
            .GetView(null, $"/Views/Feedback/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Feedback";
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
