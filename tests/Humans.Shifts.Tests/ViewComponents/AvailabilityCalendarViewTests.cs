using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using System.Net;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Users.Contracts;
using Humans.Base.Extensions;
using NodaTime;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

public class AvailabilityCalendarViewTests
{
    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task Selected_days_label_preserves_the_member_language(string language)
    {
        using var culture = new CultureScope(language);
        var html = await RenderAsync();
        var assignment = Regex.Match(html, "countEl\\.textContent = (?<expression>.*);", RegexOptions.NonBacktracking)
            .Groups["expression"].Value;
        assignment.Should().NotContain("&#", "JavaScript textContent must receive characters, not HTML entities");
        var literal = Regex.Match(html, "var daysSelectedLabel = (?<literal>.*);", RegexOptions.NonBacktracking)
            .Groups["literal"].Value;
        var expected = new ResourceManager(typeof(ShiftsResource))
            .GetString("Shifts_DaysSelected", CultureInfo.GetCultureInfo(language));
        JsonSerializer.Deserialize<string>(literal).Should().Be(expected);
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task Signup_range_summaries_preserve_the_member_language(string language)
    {
        using var culture = new CultureScope(language);
        var resources = new ResourceManager(typeof(ShiftsResource));
        var start = new LocalDate(2026, 7, 1);
        foreach (var days in new[] { 1, 2 })
        {
            var html = await RenderAsync(days);
            var key = days == 1 ? "Shifts_ConfirmSignup_Range_Single" : "Shifts_ConfirmSignup_Range";
            var template = resources.GetString(key, CultureInfo.GetCultureInfo(language))!;
            var expected = string.Format(CultureInfo.CurrentCulture, template, start.ToWeekdayDayMonth(), start.PlusDays(days - 1).ToWeekdayDayMonth(), days);

            WebUtility.HtmlDecode(html).Should().Contain(expected);
        }
    }

    private static async Task<string> RenderAsync(int signupDays = 0)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AvailabilityCalendarViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(Substitute.For<IShiftService>());
        builder.Services.AddSingleton(Substitute.For<IUserService>());
        builder.Services.AddSingleton(Substitute.For<ICantinaService>());
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddApplicationPart(typeof(ShiftsResource).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Shifts/Mine.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary());
        var rota = UrgentShiftFixtures.Rota(period: RotaPeriod.Build);
        var blockId = Guid.NewGuid();
        data.Model = new MyShiftsViewModel
        {
            Upcoming = Enumerable.Range(0, signupDays).Select(day => new MySignupItem
            {
                Signup = new ShiftUserSignup(
                    new ShiftAdminSignup(Guid.NewGuid(), Guid.NewGuid(), blockId, SignupStatus.Confirmed, Instant.FromUtc(2026, 1, 1, 0, 0)),
                    UrgentShiftFixtures.Shift(rotaId: rota.Id, dayOffset: day, isAllDay: true), rota)
            }).ToList(),
            EventSettings = new BurnSettingsInfo(Guid.NewGuid(), "Event", 2026, "Europe/Madrid",
                new LocalDate(2026, 7, 1), -1, 0, 1, -1, -1, -1, -1,
                new Dictionary<int, int>(), null, null, true),
        };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
