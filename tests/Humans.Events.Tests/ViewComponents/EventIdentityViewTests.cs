using System.Globalization;
using System.Resources;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Events.Contracts;
using Humans.Events.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
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

namespace Humans.Events.Tests.ViewComponents;

public class EventIdentityViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("nl")]
    public async Task Browser_labels_resolve_member_identity_and_preserve_authored_hosts_and_camps(string language)
    {
        using var culture = new CultureScope(language);
        var id = Guid.NewGuid();
        var unusedId = Guid.NewGuid();
        const string name = "Current <script>&";
        const string host = "Authored host <script>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = id, BurnerName = name }, [], [], null));
        users.GetUserInfoAsync(unusedId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = unusedId, BurnerName = "Unused member" }, [], [], null));
        var moderation = await RenderAsync("/Views/EventsModeration/Index.cshtml", new ModerationQueueViewModel
        {
            Events = [
                new() { Id = Guid.NewGuid(), SubmitterUserId = id, Status = EventStatus.Withdrawn },
                new() { Id = Guid.NewGuid(), SubmitterUserId = Guid.NewGuid(), Status = EventStatus.Withdrawn },
                new() { Id = Guid.NewGuid(), SubmitterUserId = unusedId, CampName = "Test Camp", Status = EventStatus.Withdrawn },
            ]
        }, users);
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        var headers = Regex.Matches(moderation, "<button class=\"accordion-button[^>]*>(?<header>.*?)</button>",
                RegexOptions.Singleline | RegexOptions.NonBacktracking)
            .Select(m => m.Groups["header"].Value).ToList();
        headers[0].Should().Contain(HtmlEncoder.Default.Encode(name));
        headers[1].Should().Contain(HtmlEncoder.Default.Encode(unknown));
        headers[2].Should().Contain("Test Camp").And.NotContain("Unused member");
        moderation.Should().NotContain("<script>");
        users.ClearReceivedCalls();
        var browse = await RenderAsync("/Views/Events/Browse.cshtml", new BrowseViewModel
        {
            DayGroups = [new() { DayLabel = "Day", Items = [
                new() { EventId = Guid.NewGuid(), SubmitterUserId = id, DurationMinutes = 60 },
                new() { EventId = Guid.NewGuid(), SubmitterUserId = unusedId, Host = host, DurationMinutes = 60 },
                new() { EventId = Guid.NewGuid(), CampName = "Test Camp", DurationMinutes = 60 },
            ] }]
        }, users);
        browse.Should().Contain(HtmlEncoder.Default.Encode(name)).And.Contain(HtmlEncoder.Default.Encode(host))
            .And.Contain("Test Camp").And.NotContain("Unused member");
        await users.DidNotReceive().GetUserInfoAsync(unusedId, Arg.Any<CancellationToken>());
    }

    private static async Task<string> RenderAsync(string path, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(EventIdentityViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(users);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        builder.Services.AddSingleton(authorization);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddApplicationPart(typeof(EventsResource).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>().GetView(null, path, isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Events";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
