using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Shifts.Models;
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
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

public class ShiftInfoViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("en", "l'heure \"du jour\"\n</script>\\suite")]
    public async Task Wizard_step_copy_round_trips_through_script(string language, string? overrideCopy = null)
    {
        using var culture = new CultureScope(language);
        var resources = new ResourceManager(typeof(ShiftsResource));
        string Copy(string key) => overrideCopy ?? resources.GetString(key, CultureInfo.CurrentUICulture)!;
        var localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), Copy(call.Arg<string>())));
        var script = await RenderScriptAsync(localizer);
        var literal = Regex.Match(script, @"const STEPS = (?<literal>.*?);", RegexOptions.Singleline | RegexOptions.NonBacktracking)
            .Groups["literal"].Value;
        using var steps = JsonDocument.Parse(literal);
        var families = new[] { "Skills", "WorkStyle", "Languages" };
        for (var i = 0; i < families.Length; i++)
        {
            steps.RootElement[i].GetProperty("label").GetString().Should()
                .Be(string.Format(CultureInfo.CurrentCulture, Copy("Shifts_ShiftInfo_StepOf"), i + 1, 3));
            steps.RootElement[i].GetProperty("title").GetString().Should().Be(Copy($"Shifts_ShiftInfo_{families[i]}Title"));
            steps.RootElement[i].GetProperty("subtitle").GetString().Should().Be(Copy($"Shifts_ShiftInfo_{families[i]}Subtitle"));
        }
        literal.Should().NotContain("</script>");
    }

    private static async Task<string> RenderScriptAsync(IStringLocalizer<ShiftsResource> localizer)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ShiftInfoViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(localizer);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ShiftsResource).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/ShiftProfile/ShiftInfo.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = new ShiftInfoViewModel(),
        };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        await ((RazorView)result.View).RazorPage.SectionWriters["Scripts"]();
        return writer.ToString();
    }
}
