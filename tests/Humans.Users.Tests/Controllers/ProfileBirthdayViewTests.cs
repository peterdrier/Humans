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
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task BirthdayMonthNames_UseUiLanguageAndKeepNumericValues(string language)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var july = CultureInfo.CurrentUICulture.DateTimeFormat.GetMonthName(7);
        var html = await RenderAsync("Edit", new ProfileViewModel { BirthdayMonth = 7, BirthdayDay = 1 });

        html.Should().Contain($"value=\"7\" selected=\"selected\">{HtmlEncoder.Default.Encode(july)}</option>");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansFact]
    public void BirthdayDisplay_ContainsOnlyLocalizedMonthAndDay()
    {
        using var culture = new CultureScope("es");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var expected = $"29 {CultureInfo.CurrentUICulture.DateTimeFormat.AbbreviatedMonthGenitiveNames[1]}";

        new ProfileCardViewModel { BirthdayMonth = 2, BirthdayDay = 29 }
            .FormattedBirthday.Should().Be(expected);
        new ProfileViewModel { BirthdayMonth = 2, BirthdayDay = 29 }
            .FormattedBirthday.Should().Be(expected);
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task LocationCoordinates_UseUiCultureAndKeepEnglishInputValues(string language)
    {
        using var inputCulture = new CultureScope("en");
        var ui = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var coordinates = $"({41.2345.ToString("F4", ui)}, {(-1.2345).ToString("F4", ui)})";
        var html = await RenderAsync("Edit", new ProfileViewModel
        {
            City = "Example",
            CountryCode = "ES",
            Latitude = 41.2345,
            Longitude = -1.2345,
        });

        html.Should().Contain(HtmlEncoder.Default.Encode(coordinates));
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
        builder.Services.AddSingleton(Substitute.For<IUserService>());
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
