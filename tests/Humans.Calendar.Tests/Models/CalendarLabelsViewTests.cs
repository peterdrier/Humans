using Humans.AuditLog.Contracts;
using Humans.AuditLog.ViewComponents;
using Humans.Calendar.Services.Dtos;
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
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task MonthHeadings_UseUiLanguageWithEnglishInputCulture(string language)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var model = new CalendarMonthViewModel(new YearMonth(2026, 7), [], null, "Europe/Madrid");
        var month = CultureInfo.GetCultureInfo(language).DateTimeFormat.GetMonthName(7);

        foreach (var page in new[] { "Index", "List", "Team" })
        {
            var html = await RenderAsync(page, model);
            html.Should().Contain($"2026 {HtmlEncoder.Default.Encode(month)}</h1>", page);
        }
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task WeekdayLabels_UseUiLanguageAndPreserveRecurrenceCodes(string language)
    {
        using var inputCulture = new CultureScope("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        var abbreviated = CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(DayOfWeek.Monday);
        var monday = CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(DayOfWeek.Monday);
        var grid = await RenderAsync("Index", new CalendarMonthViewModel(new YearMonth(2026, 7), [], null, "Europe/Madrid"));
        grid.Should().Contain($">{HtmlEncoder.Default.Encode(abbreviated)}</th>");

        var form = await RenderAsync("_CalendarEventFormFields", new CalendarEventFormViewModel());
        form.Should().Contain($"for=\"rec-wd-MO\">{HtmlEncoder.Default.Encode(abbreviated)}</label>");
        var dayNames = System.Text.RegularExpressions.Regex.Match(form, "data-day-names=\"(?<days>[^\"]+)\"", System.Text.RegularExpressions.RegexOptions.NonBacktracking).Groups["days"].Value;
        dayNames.Split('|')[1].Should().Be(HtmlEncoder.Default.Encode(monday));
        form.Should().Contain("value=\"MO\" data-weekday");
        CultureInfo.CurrentCulture.Name.Should().Be("en");
    }

    [HumansFact]
    public async Task Event_EmptyHistory_UsesHostLanguage()
    {
        using var culture = new CultureScope("en");
        var now = Instant.FromUtc(2026, 7, 1, 0, 0);
        var detail = new CalendarEventDetail(Guid.NewGuid(), "Fixture", null, null, null,
            Guid.NewGuid(), now, now + Duration.FromHours(1), false, null, null, now, now);
        var model = new CalendarEventViewModel(detail, "Team", [], false, "Europe/Madrid");

        var html = await RenderAsync("Event", model);

        html.Should().Contain("No audit history.");
    }

    [HumansFact]
    public async Task CommunitySources_UseUiLanguageAcrossCalendarViews()
    {
        using var culture = new CultureScope(CultureCatalog.CanonicalLegalCultureCode);
        var sourceLabel = new System.Resources.ResourceManager(typeof(CalendarResource))
            .GetString("Calendar_Source_Workgroups", CultureInfo.CurrentUICulture);
        sourceLabel.Should().NotBe("Workgroups");
        var start = Instant.FromUtc(2026, 7, 6, 10, 0);
        var single = new CalendarOccurrence(Guid.Empty, start, start + Duration.FromHours(1), false,
            "Meeting", null, null, null, Guid.Empty, "", false, null, Source: "Workgroups");
        var banner = single with { Title = "Workshop", OccurrenceEndUtc = start + Duration.FromDays(2) };
        var unknown = single with { Title = "Other", Source = "New contributor" };
        CalendarOccurrence[] items = [single, banner, unknown];
        var month = new CalendarMonthViewModel(new YearMonth(2026, 7), items, null, "Europe/Madrid");

        foreach (var page in new[] { "Index", "List", "Team", "Agenda" })
        {
            object model = string.Equals(page, "Agenda", StringComparison.Ordinal)
                ? new CalendarAgendaViewModel(start, start + Duration.FromDays(4), items, "Europe/Madrid")
                : month;
            var html = System.Net.WebUtility.HtmlDecode(await RenderAsync(page, model));
            html.Should().Contain($"— {sourceLabel}", page);
            html.Should().Contain("New contributor", page);
            html.Should().NotContain("Calendar_Source_", page);
            html.Should().NotContain("— Workgroups", page);
        }
        single.Source.Should().Be("Workgroups");
    }

    private static async Task<string> RenderAsync(string page, object model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CalendarLabelsViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddSingleton(Substitute.For<IAuditViewerService>());
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(CalendarResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddApplicationPart(typeof(AuditLogViewComponent).Assembly);
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
