using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Backdoor.Models;
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
using NodaTime;
using NSubstitute;

using Humans.Backdoor.Controllers;
namespace Humans.Backdoor.Tests.Controllers;

public sealed class BackdoorIdentityViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("nl")]
    public async Task KeyOwners_RenderCanonicalIdentity_AndPreserveKeyStatus(string language)
    {
        using var culture = new CultureScope(language);
        var known = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(known, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = known, BurnerName = "Current <b>&" }, [], [], null));
        var at = Instant.FromUtc(2026, 1, 1, 12, 0);
        var model = new BackdoorKeysViewModel
        {
            Keys = [
                new BackdoorKeyListItem(Guid.NewGuid(), known, "laptop", "hum_active", at, null, null, true),
                new BackdoorKeyListItem(Guid.NewGuid(), missing, "retired", "hum_revoked", at, null, at, false),
            ],
        };
        var html = await RenderAsync("Index", model, users);
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        html.Should().Contain(HtmlEncoder.Default.Encode("Current <b>&"));
        html.Should().Contain(HtmlEncoder.Default.Encode(unknown));
        html.Should().NotContain("Current <b>&");
        html.Should().Contain("hum_active").And.Contain("hum_revoked").And.Contain("Active").And.Contain("Revoked");
    }

    private static async Task<string> RenderAsync(string page, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BackdoorIdentityViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(BackdoorController).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        builder.Services.AddSingleton(users);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, $"/Views/Backdoor/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Backdoor";
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
