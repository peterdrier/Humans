using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Cantina page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Cantina</c>
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
/// The resx carve moved all 44 <c>Cantina_*</c> keys out of <c>SharedResource</c>. A key the
/// carve missed, or a call site bound to the wrong set, renders the raw key — in every
/// language, with no error. The Spanish request is the only thing that proves the section
/// RCL's satellite assemblies reach the host's probing path at all.
/// </description></item>
/// <item><description>
/// Both CSV writers moved out of <c>Humans.Web</c> with the controller, and neither is reached
/// by a view. One GET each is what proves the export routes still resolve.
/// </description></item>
/// </list>
/// <para>
/// The fixture is a database with no active event: every page falls through to its
/// no-data branch, which still renders the header, the aggregate cards, the per-day table
/// headings and the empty state — 12 of the section's keys and every tag helper on the page.
/// Seeding a burn plus confirmed signups would add the person rows and nothing else this
/// test can catch; the aggregation itself is <c>Humans.Cantina.Tests</c>' business.
/// </para>
/// </remarks>
public class CantinaPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static (string Url, string Copy)[] Pages =>
    [
        ("/Cantina/Roster", "Cantina weekly roster"),   // Cantina_Roster_PageTitle
        ("/Cantina/Roster/Day", "humans on site"),      // Cantina_Day_TotalOnSite
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_cantina_page_renders_its_own_copy_with_tag_helpers_applied()
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

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            // The fallback for a key the carve missed is the key itself.
            html.Should().NotContain("Cantina_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_weekly_roster_renders_its_aggregate_cards_and_per_day_table()
    {
        // The keys on this page span three of the four prefixes the carve took, and the per-day
        // table is the section's only <partial> — _Table lives in Humans.UI and resolves by name
        // across application parts, which is exactly the kind of lookup a move can break
        // silently (the partial simply renders nothing).
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Cantina/Roster", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("unique humans on site this week");  // Cantina_Roster_TotalOnSite
        html.Should().Contain("Dietary breakdown");                // Cantina_Roster_DietaryBreakdown
        html.Should().Contain("By day");                           // Cantina_Roster_PerDayTable_Heading
        html.Should().Contain("No humans signed up for this week"); // Cantina_Roster_EmptyState
        html.Should().Contain("day-table",
            because: "the per-day <partial name=\"_Table\"> must resolve across application parts");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Both_csv_exports_stream_from_the_sections_own_writers()
    {
        // CantinaRosterCsvWriter and CantinaDailyMatrixCsvWriter moved out of Humans.Web with
        // the controller. No view reaches them, so nothing else in the suite would notice the
        // routes breaking.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in new[] { "/Cantina/Roster/Csv", "/Cantina/Roster/Day/Csv" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must export");
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_roster_or_the_export()
    {
        // The section's negative access rule: an authenticated human without
        // Admin/CantinaAdmin never sees the roster. Worth pinning at the move because the
        // controller is now an internal type in another assembly, routed through
        // SectionControllerFeatureProvider, while CantinaAdminOrAdmin stayed in Shell's
        // AuthorizationPolicyExtensions (design §8's asymmetry).
        //
        // The shape is a 302 to Program.cs's AccessDeniedPath, not the bare 403 the invariants
        // doc claimed before this move — cookie authentication's DenyAnonymousAuthorization
        // handler redirects rather than writing a status, and that is app-wide and predates
        // G5. Asserted as it behaves, and corrected in Docs/Cantina.md rather than left as a
        // doc that describes an app nobody ships.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[] { "/Cantina/Roster", "/Cantina/Roster/Day", "/Cantina/Roster/Csv" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"GET {url} must be denied, not sent to sign in");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Cantina_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the
        // neutral set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities ("únicas" reaches
        // the body as "&#xFA;nicas"), so the assertions stay on ASCII-only runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Cantina page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Cantina/Roster", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Cantina/Roster", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Roster semanal de cantina");  // Cantina_Roster_PageTitle
        html.Should().Contain("Desglose alimentario");       // Cantina_Roster_DietaryBreakdown
        html.Should().Contain("No hay evento activo");       // Cantina_Roster_NoActiveEvent
        html.Should().NotContain("Cantina weekly roster");
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
}
