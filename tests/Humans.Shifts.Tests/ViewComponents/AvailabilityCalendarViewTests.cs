using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
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
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
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

    private static async Task<string> RenderAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AvailabilityCalendarViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(Substitute.For<IShiftManagementServiceRead>());
        builder.Services.AddSingleton(Substitute.For<IUserServiceRead>());
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
        data.Model = new MyShiftsViewModel
        {
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
