using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every City Planning page through the real app, as the standing form of the §15
/// step 12 check for the section's move into <c>src/Sections/Humans.CityPlanning</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>, so
/// "the page loads" is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops.
/// </description></item>
/// <item><description>
/// The resx carve moved 103 <c>CityPlanning_*</c> keys out of <c>SharedResource</c>. A key the
/// carve missed, or a call site bound to the wrong set, renders the raw key — in every
/// language, with no error. The Spanish request is the only thing that proves the section
/// RCL's satellite assemblies reach the host's probing path at all.
/// </description></item>
/// <item><description>
/// A static asset that moved to the section's RCL is served from the static-web-assets manifest
/// rather than from <c>wwwroot</c> on disk, so <c>asp-append-version</c> can silently emit no
/// <c>?v=</c> hash while the page still returns 200 (§15 step 7).
/// </description></item>
/// </list>
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
        ($"/CityPlanning/ContainerMap/{Year}", "Container legend"),                 // ContainerMap_Legend_Title
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

            // A view component element that the section's _ViewImports failed to bind renders
            // as literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // The fallback for a key the carve missed is the key itself.
            html.Should().NotContain("CityPlanning_", $"GET {url} rendered a raw resource key");
            html.Should().NotContain("ContainerMap_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_admin_page_renders_the_shell_resident_access_matrix_widget()
    {
        // <vc:access-matrix> cannot be used from a section view (AccessMatrixViewComponent is in
        // Humans.Web and a section _ViewImports cannot @addTagHelper it). Component.InvokeAsync
        // resolves it by name across application parts instead — this pins that it still lands.
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
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities ("colocación"
        // renders as "colocaci&#xF3;n"), so the assertions stay on ASCII-only runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every City Planning page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/CityPlanning", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
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
        // §15 step 7: asp-append-version resolves the file through the host's IFileProvider.
        // Once the file lives in an RCL it comes from the static-web-assets manifest instead of
        // wwwroot on disk, and a provider that cannot see it emits the bare src with no ?v=
        // rather than failing.
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
        // The other half of §15 step 7: the src resolving is not the same as the file being
        // there. Before the test host composed the static-web-assets manifest an RCL asset
        // 404'd here while the page still returned 200.
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
        // Shell's Views/Camp/Details.cshtml is the one place outside the section that reads
        // CityPlanning_* keys and renders the section's _PlacementHelpModal. Both cross the
        // assembly boundary: the view injects IStringLocalizer<CityPlanningResource>, and the
        // partial is found by NAME across application parts now that it lives in the RCL's
        // Views/Shared. A path-based Html.PartialAsync would have thrown; a raw key would not.
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
