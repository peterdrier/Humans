using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Users.Models;
using Humans.Users.Contracts;
using System.Globalization;
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

namespace Humans.Users.Tests.Controllers;

public class ProfileBirthdayViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en", "July")]
    [Xunit.InlineData("es", "julio")]
    [Xunit.InlineData("de", "Juli")]
    [Xunit.InlineData("it", "luglio")]
    [Xunit.InlineData("fr", "juillet")]
    [Xunit.InlineData("ca", "juliol")]
    public async Task BirthdayMonthNames_UseUiLanguageAndKeepNumericValues(string language, string july)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var html = await RenderAsync("Edit", new ProfileViewModel { BirthdayMonth = 7, BirthdayDay = 1 });

        html.Should().Contain($"value=\"7\" selected=\"selected\">{HtmlEncoder.Default.Encode(july)}</option>");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.InlineData("en", "(41.2345, -1.2345)")]
    [Xunit.InlineData("es", "(41,2345, -1,2345)")]
    [Xunit.InlineData("de", "(41,2345, -1,2345)")]
    [Xunit.InlineData("it", "(41,2345, -1,2345)")]
    [Xunit.InlineData("fr", "(41,2345, -1,2345)")]
    [Xunit.InlineData("ca", "(41,2345, -1,2345)")]
    public async Task LocationCoordinates_UseUiCultureAndKeepEnglishInputValues(string language, string coordinates)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var html = await RenderAsync("Edit", new ProfileViewModel
        {
            City = "Example",
            CountryCode = "ES",
            Latitude = 41.2345,
            Longitude = -1.2345,
        });

        html.Should().Contain(coordinates);
        html.Should().Contain("value=\"41.2345\"").And.Contain("value=\"-1.2345\"");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProfileBirthdayViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddSingleton(Substitute.For<IUserServiceRead>());
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(UsersResource).Assembly)
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
            .GetView(null, $"/Views/Profile/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Profile";
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
