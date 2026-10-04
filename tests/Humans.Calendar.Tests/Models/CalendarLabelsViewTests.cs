using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Calendar.Models;
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
using NodaTime;
using NSubstitute;

namespace Humans.Calendar.Tests.Models;

public class CalendarLabelsViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en", "July")]
    [Xunit.InlineData("es", "julio")]
    [Xunit.InlineData("de", "Juli")]
    [Xunit.InlineData("it", "luglio")]
    [Xunit.InlineData("fr", "juillet")]
    [Xunit.InlineData("ca", "juliol")]
    public async Task MonthHeadings_UseUiLanguageWithEnglishInputCulture(string language, string month)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var model = new CalendarMonthViewModel(new YearMonth(2026, 7), [], null, "Europe/Madrid");

        foreach (var page in new[] { "Index", "List", "Team" })
        {
            var html = await RenderAsync(page, model);
            html.Should().Contain($"2026 {HtmlEncoder.Default.Encode(month)}</h1>", page);
        }
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.InlineData("en", "Mon", "Monday")]
    [Xunit.InlineData("es", "lun", "lunes")]
    [Xunit.InlineData("de", "Mo", "Montag")]
    [Xunit.InlineData("it", "lun", "lunedì")]
    [Xunit.InlineData("fr", "lun.", "lundi")]
    [Xunit.InlineData("ca", "dl.", "dilluns")]
    public async Task WeekdayLabels_UseUiLanguageAndPreserveRecurrenceCodes(
        string language, string abbreviated, string monday)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var grid = await RenderAsync("Index", new CalendarMonthViewModel(new YearMonth(2026, 7), [], null, "Europe/Madrid"));
        grid.Should().Contain($">{HtmlEncoder.Default.Encode(abbreviated)}</th>");

        var form = await RenderAsync("_CalendarEventFormFields", new CalendarEventFormViewModel());
        form.Should().Contain($"for=\"rec-wd-MO\">{HtmlEncoder.Default.Encode(abbreviated)}</label>");
        var dayNames = System.Text.RegularExpressions.Regex.Match(form, "data-day-names=\"(?<days>[^\"]+)\"", System.Text.RegularExpressions.RegexOptions.NonBacktracking).Groups["days"].Value;
        dayNames.Split('|')[1].Should().Be(HtmlEncoder.Default.Encode(monday));
        form.Should().Contain("value=\"MO\" data-weekday");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CalendarLabelsViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(CalendarResource).Assembly)
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
            .GetView(null, $"/Views/Calendar/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Calendar";
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
