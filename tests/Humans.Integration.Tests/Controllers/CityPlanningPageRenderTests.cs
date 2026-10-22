using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every City Planning page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// only Spanish proves the satellites ship, and an RCL asset can lose its <c>?v=</c> hash silently.
/// </remarks>
public class CityPlanningPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string MainJsPath = "/_content/Humans.CityPlanning/js/city-planning/main.js";
    private const string BarrioMapJsPath = "/_content/Humans.CityPlanning/js/city-planning/barrio-map/main.js";
    private const string ContainerMapJsPath = "/_content/Humans.CityPlanning/js/city-planning/container-map/main.js";
    private const string AdminImportJsPath = "/_content/Humans.CityPlanning/js/city-planning/barrio-map/admin-import.js";
    private const string HelpImagePath = "/_content/Humans.CityPlanning/img/city-planning/barrio-draw.png";

    private static readonly int Year = 2026;

    private static (string Url, string Copy)[] Pages =>
    [
        ("/CityPlanning", "Barrio zones"),                                          // CityPlanning_BarrioZones
        ("/CityPlanning/BarrioMap", "Barrio placement map"),                        // CityPlanning_BarrioMapTitle
        ("/CityPlanning/BarrioMap/Admin", "Barrio Map Admin"),                      // CityPlanning_Admin_Title
        ($"/CityPlanning/ContainerMap/{Year}", "Container legend"),                 // Containers_MapLegend_Title
        ($"/CityPlanning/BarrioMap/Admin/Containers/{Year}", "Container placement"),   // CityPlanning_Admin_ContainerPlacement
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_city_planning_page_renders_its_own_copy_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own resolved copy");

            html.Should().NotContain("CityPlanning_", $"GET {url} rendered a raw resource key");
            html.Should().NotContain("Containers_Map", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_admin_page_renders_the_access_matrix_widget()
    {
        // An unbound <vc:> ships as inert markup, so the emitted modal id is the proof.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/CityPlanning/BarrioMap/Admin", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("sectionHelp-CityPlanningBarrioMap",
            because: "the access-matrix component renders a modal id built from the section key; "
                   + "a component that failed to resolve renders nothing at all");
    }

    [HumansFact(Timeout = 120000)]
    public async Task City_planning_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // Only Spanish proves the satellites shipped. HtmlEncoder escapes non-ASCII ("colocación" becomes
        // "colocaci&#xF3;n"), so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/CityPlanning", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/CityPlanning/BarrioMap/Admin", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Admin del Mapa de Barrios");   // CityPlanning_Admin_Title
        html.Should().Contain("Fase de Colocaci");            // CityPlanning_Admin_PlacementPhaseCard
        html.Should().NotContain("Barrio Map Admin");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Map_pages_link_the_sections_static_assets_with_cache_busting_hashes()
    {
        // An RCL asset comes from the static-web-assets manifest; a file provider that can't see it emits the bare src
        // with no ?v= rather than failing.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var expectations = new (string Url, string Asset)[]
        {
            ("/CityPlanning", MainJsPath),
            ("/CityPlanning/BarrioMap", BarrioMapJsPath),
            ("/CityPlanning/BarrioMap/Admin", AdminImportJsPath),
            ($"/CityPlanning/ContainerMap/{Year}", ContainerMapJsPath),
        };

        foreach (var (url, asset) in expectations)
        {
            var html = await (await Client.GetAsync(url, ct)).Content.ReadAsStringAsync(ct);
            html.Should().Contain($"src=\"{asset}?v=", $"GET {url} must cache-bust {asset}");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_sections_static_assets_are_served_from_its_own_RCL()
    {
        // A src resolving doesn't mean the file is served.
        var ct = Xunit.TestContext.Current.CancellationToken;

        foreach (var asset in new[] { MainJsPath, BarrioMapJsPath, ContainerMapJsPath, AdminImportJsPath, HelpImagePath })
        {
            var response = await Client.GetAsync(asset, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {asset} must be served");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_camps_page_renders_city_planning_copy_and_the_sections_help_partial()
    {
        // Shell's Camp Details is the one outside reader of CityPlanning_* keys and the section's _PlacementHelpModal,
        // found by name across parts. A raw key wouldn't throw.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var listHtml = await (await Client.GetAsync("/Camp", ct)).Content.ReadAsStringAsync(ct);
        var slug = ExtractFirstCampSlug(listHtml);
        if (slug is null)
        {
            return; // no camp seeded in this database; the section's own pages carry the check
        }

        var response = await Client.GetAsync($"/Camp/{slug}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("CityPlanning_",
            because: "Camp/Details.cshtml resolves the section's carved keys through "
                   + "IStringLocalizer<CityPlanningResource>, not the ambient SharedResource one");
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }

    private static string? ExtractFirstCampSlug(string html)
    {
        const string marker = "href=\"/Camp/";
        var index = html.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var start = index + marker.Length;
        var end = html.IndexOf('"', start);
        if (end < 0) return null;
        var slug = html[start..end];
        return slug.Length == 0 || slug.Contains('/', StringComparison.Ordinal) ? null : slug;
    }
}
