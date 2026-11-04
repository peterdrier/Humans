using System.Globalization;
using System.Resources;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Gate.Controllers;
using Humans.Gate.Models;
using Humans.Gate.Services;
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

namespace Humans.Gate.Tests;

public class GateIdentityViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("nl")]
    public async Task Claim_and_PIN_labels_resolve_names_without_changing_roster_ids_or_PIN_mode(string language)
    {
        using var culture = new CultureScope(language);
        var id = Guid.NewGuid();
        var missing = Guid.NewGuid();
        const string name = "Current <script>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = id, BurnerName = name }, [], [], null));
        var claim = await RenderAsync("/Views/Gate/Claim.cshtml", new GateClaimViewModel([id, missing]), users);
        var picks = Regex.Matches(claim, "<button[^>]*name=\"userId\"[^>]*>(?<caption>.*?)</button>",
            RegexOptions.Singleline | RegexOptions.NonBacktracking);
        picks.Should().HaveCount(2);
        picks[0].Value.Should().Contain($"value=\"{id}\"").And.Contain(HtmlEncoder.Default.Encode(name));
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        picks[1].Value.Should().Contain($"value=\"{missing}\"").And.Contain(HtmlEncoder.Default.Encode(unknown));
        foreach (var hasPin in new[] { false, true })
        {
            var pin = await RenderAsync("/Views/Gate/Pin.cshtml", GatePinViewModel.ForClaim(id, new GatePinStatus(hasPin, false)), users);
            pin.Should().Contain(HtmlEncoder.Default.Encode(name)).And.Contain($"value=\"{id}\"")
                .And.NotContain("<script>");
            pin.Contains("data-gate-confirm>", StringComparison.Ordinal).Should().Be(!hasPin);
            if (!hasPin)
                Regex.Match(pin, "<strong>(?<identity>.*?)</strong>", RegexOptions.Singleline | RegexOptions.NonBacktracking)
                    .Groups["identity"].Value.Should().Contain(HtmlEncoder.Default.Encode(name));
        }
    }

    private static async Task<string> RenderAsync(string path, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GateIdentityViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton(Substitute.For<IFileVersionProvider>());
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
            .AddApplicationPart(typeof(GateController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>().GetView(null, path, isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Gate";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
