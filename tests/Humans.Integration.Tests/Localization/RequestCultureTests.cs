using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Request culture through the real pipeline (peterdrier/Humans#2581). Guards pass today and must keep passing once
/// <see cref="CultureInfo.CurrentCulture"/> follows the user; broken facts prove what is wrong today.
/// </summary>
public sealed class RequestCultureTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string Issue = "peterdrier/Humans#2581";

    protected override HumansWebApplicationFactory CreateFactory(string connectionString) =>
        new ProbeFactory(connectionString);

    [HumansFact(Timeout = 60000)]
    public async Task FormPost_DotDecimal_BindsUnderEveryCulture()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        foreach (var culture in CultureCatalog.SupportedCultureCodes)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Amount"] = "60.50",
                ["Latitude"] = "40.4168",
                ["Date"] = "2026-06-05",
            });

            var echo = await SendAsync(HttpMethod.Post, "/culture-probe/bind", culture, ct, form);

            echo.Should().Be($"{culture}|60.50|40.4168|2026-06-05", "culture {0}", culture);
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task QueryAndRoute_InvariantValues_BindUnderEveryCulture()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        foreach (var culture in CultureCatalog.SupportedCultureCodes)
        {
            var echo = await SendAsync(HttpMethod.Get,
                "/culture-probe/bind/60.50?latitude=40.4168&date=2026-06-05", culture, ct);

            echo.Should().Be($"{culture}|60.50|40.4168|2026-06-05", "culture {0}", culture);
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task MachineInputs_RenderInvariantUnderEveryCulture()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        foreach (var culture in CultureCatalog.SupportedCultureCodes)
        {
            var values = await SendAsync(HttpMethod.Get, "/culture-probe/inputs", culture, ct);

            values.Should().StartWith($"{culture}|number=60.50|date=2026-06-05|hidden=40.4168|",
                "culture {0}", culture);
        }
    }

    [BrokenFact(Issue, Timeout = 60000)]
    public async Task Request_SpanishUser_FormatsInSpanish()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var formatted = await SendAsync(HttpMethod.Get, "/culture-probe/implicit", "es", ct);

        formatted.Should().Be($"es|60,5|{new DateTime(2026, 6, 5).ToString(CultureInfo.GetCultureInfo("es"))}");
    }

    [BrokenFact(Issue, Timeout = 60000)]
    public async Task TextDecimalInput_CommaCulture_RoundTrips()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var inputs = await SendAsync(HttpMethod.Get, "/culture-probe/inputs", "es", ct);
        var shown = inputs.Split('|').Single(v => v.StartsWith("text=", StringComparison.Ordinal))["text=".Length..];
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Amount"] = shown,
            ["Latitude"] = "40.4168",
            ["Date"] = "2026-06-05",
        });

        var echo = await SendAsync(HttpMethod.Post, "/culture-probe/bind", "es", ct, form);

        shown.Should().Be("60,50");
        echo.Should().Be("es|60.50|40.4168|2026-06-05");
    }

    private async Task<string> SendAsync(HttpMethod method, string url, string culture, CancellationToken ct,
        HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body };
        request.Headers.AcceptLanguage.ParseAdd(culture);
        using var response = await Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private sealed class ProbeFactory(string connectionString) : HumansWebApplicationFactory(connectionString)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(CultureProbeController).Assembly));
        }
    }
}

public sealed class CultureProbeModel
{
    public decimal Amount { get; set; } = 60.50m;
    public double Latitude { get; set; } = 40.4168;
    public DateTime Date { get; set; } = new(2026, 6, 5);
}

/// <summary>Echoes what binding and the input tag helper produce under the request culture.</summary>
[AllowAnonymous]
[Route("culture-probe")]
public sealed class CultureProbeController : Controller
{
    [HttpPost("bind")]
    public IActionResult BindForm([FromForm] CultureProbeModel model) => Content(Echo(model));

    [HttpGet("bind/{amount}")]
    public IActionResult BindQuery(decimal amount, double latitude, DateTime date) =>
        Content(Echo(new CultureProbeModel { Amount = amount, Latitude = latitude, Date = date }));

    [HttpGet("implicit")]
    public IActionResult Implicit() =>
        Content($"{CultureInfo.CurrentUICulture.Name}|{60.5m}|{new DateTime(2026, 6, 5)}");

    [HttpGet("inputs")]
    public IActionResult Inputs() => Content(string.Join('|',
        CultureInfo.CurrentUICulture.Name,
        "number=" + RenderValue(nameof(CultureProbeModel.Amount), "number"),
        "date=" + RenderValue(nameof(CultureProbeModel.Date), "date"),
        "hidden=" + RenderValue(nameof(CultureProbeModel.Latitude), "hidden"),
        "text=" + RenderValue(nameof(CultureProbeModel.Amount), null)));

    private static string Echo(CultureProbeModel model) => string.Join('|',
        CultureInfo.CurrentUICulture.Name,
        model.Amount.ToString("0.00", CultureInfo.InvariantCulture),
        model.Latitude.ToString(CultureInfo.InvariantCulture),
        model.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    // The same InputTagHelper an <input asp-for> in a view runs.
    private string RenderValue(string property, string? type)
    {
        var services = HttpContext.RequestServices;
        var metadata = services.GetRequiredService<IModelMetadataProvider>();
        var model = new CultureProbeModel();
        var viewData = new ViewDataDictionary<CultureProbeModel>(metadata, ModelState) { Model = model };
        var viewContext = new ViewContext(ControllerContext, new NoView(), viewData, TempData, TextWriter.Null,
            new HtmlHelperOptions());
        var explorer = metadata.GetModelExplorerForType(typeof(CultureProbeModel), model).GetExplorerForProperty(property);
        var attributes = new TagHelperAttributeList();
        if (type is not null) attributes.Add("type", type);
        var helper = new InputTagHelper(services.GetRequiredService<IHtmlGenerator>())
        {
            ViewContext = viewContext,
            For = new ModelExpression(property, explorer),
            InputTypeName = type,
        };
        var context = new TagHelperContext(attributes, new Dictionary<object, object>(), "probe");
        var output = new TagHelperOutput("input", [.. attributes],
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        helper.Init(context);
        helper.Process(context, output);

        return output.Attributes["value"].Value.ToString() ?? string.Empty;
    }

    private sealed class NoView : IView
    {
        public string Path => string.Empty;

        public Task RenderAsync(ViewContext context) => Task.CompletedTask;
    }
}
