using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
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

public class VolunteerSearchScriptTests
{
    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public async Task Search_labels_round_trip_through_rendered_script_literals(string language)
    {
        var resources = new ResourceManager(typeof(ShiftsResource));
        var culture = CultureInfo.GetCultureInfo(language);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["NoResultsText"] = resources.GetString("ShiftDash_NoHumansFound", culture),
            ["ErrorText"] = resources.GetString("ShiftDash_SearchFailed", culture),
            ["OverlapLabel"] = resources.GetString("ShiftDash_ScheduleConflict", culture),
            ["SearchUrl"] = "/search?a=1&b=2",
            ["VoluntellUrl"] = "/assign?a=1&b=2",
        };
        var script = await RenderAsync(values);
        foreach (var (key, value) in values)
        {
            var variable = char.ToLowerInvariant(key[0]) + key[1..];
            var literal = Regex.Match(script, $"var {variable} = (?<literal>.*);", RegexOptions.NonBacktracking).Groups["literal"].Value;
            JsonSerializer.Deserialize<string>(literal).Should().Be((string?)value);
        }
    }

    [HumansFact]
    public async Task Search_labels_escape_script_delimiters_and_control_characters()
    {
        const string label = "l'heure \"du jour\"\n</script>\\suite";
        var script = await RenderAsync(new Dictionary<string, object?>(StringComparer.Ordinal) { ["ErrorText"] = label });
        var literal = Regex.Match(script, "var errorText = (?<literal>.*);", RegexOptions.NonBacktracking).Groups["literal"].Value;
        JsonSerializer.Deserialize<string>(literal).Should().Be(label);
        script.Should().NotContain("</script>");
    }

    private static async Task<string> RenderAsync(Dictionary<string, object?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(VolunteerSearchScriptTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(HumanViewComponent).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Shared/_VolunteerSearchScript.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary());
        foreach (var (key, value) in values) data[key] = value;
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
