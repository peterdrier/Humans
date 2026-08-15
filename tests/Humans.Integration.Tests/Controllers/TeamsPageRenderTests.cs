using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Teams page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Teams</c>
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
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops. The section's
/// views open with four of them (<c>human</c>, <c>human-search</c>, <c>audit-log</c> and
/// <c>access-matrix</c>), all four bound through <c>@@addTagHelper *, Humans.UI</c>.
/// </description></item>
/// <item><description>
/// The resx carve moved 193 keys across thirteen prefixes out of <c>SharedResource</c>. A key
/// the carve missed, or a call site left on the wrong localizer, renders the raw key — in every
/// language, with no error. Five keys deliberately stayed shared (<c>Teams_Title</c>,
/// <c>Teams_Member</c>, <c>MyTeams_View</c>, <c>MyTeams_Role</c>, <c>TeamDetail_Actions</c>),
/// so the assertion sweeps the carved prefixes and the shared ones both.
/// </description></item>
/// <item><description>
/// The Spanish request is the only thing that proves the section RCL's satellite assemblies
/// reach the host's probing path at all; an English-only check passes either way.
/// </description></item>
/// </list>
/// <para>
/// The fixture is the seeded dev personas and nothing else. Every page falls through to its
/// no-data or few-teams branch, which still renders the header, the nav, the table headings and
/// the empty state — and <c>CachingTeamService</c> is a Singleton that warms at startup, before
/// the test body, so seeding through <c>TeamsDbContext</c> here would be invisible to every read
/// anyway (Calendar's finding).
/// </para>
/// </remarks>
public class TeamsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly string[] CarvedPrefixes =
    [
        "Teams_", "TeamDetail_", "TeamAdmin_", "AdminTeams_", "MyTeams_", "Roster_",
        "JoinTeam_", "Birthdays_", "Team_", "AdminCreateTeam_", "AdminEditTeam_",
        "EditTeamPage_", "Map_",
    ];

    private static (string Url, string Copy)[] Pages =>
    [
        ("/Teams", "Birthday Calendar"),           // Birthdays_Title, on the directory's nav strip
        ("/Teams/My", "My Teams"),                 // MyTeams_Title
        ("/Teams/Roster", "Team Roster"),          // Roster_Title
        ("/Teams/Birthdays", "Birthday Calendar"), // Birthdays_Title
        ("/Teams/Map", "Humans Map"),              // Map_Title
        ("/Teams/Summary", "Teams Summary"),       // Shell-era literal, not a carved key
        ("/Teams/Create", "Create Team"),          // AdminCreateTeam_Title
    ];

    [HumansFact(Timeout = 180000)]
    public async Task Every_teams_page_renders_with_tag_helpers_applied_and_no_raw_keys()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            foreach (var prefix in CarvedPrefixes)
            {
                html.Should().NotContain(prefix, $"GET {url} rendered a raw {prefix}* resource key");
            }

            // The five keys that deliberately stayed in SharedResource, plus the ordinary
            // shared vocabulary the section still reads through SharedLocalizer.
            html.Should().NotContain("Common_", $"GET {url} rendered a raw shared key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_directory_renders_the_access_matrix_tag_helper()
    {
        // AccessMatrixViewComponent moved into Humans.UI (nobodies-collective/Humans#1056),
        // so the call site is <vc:access-matrix section="Teams" /> bound through
        // @addTagHelper *, Humans.UI. An unbound <vc:> ships as inert literal markup with a
        // green build and no log line, so the emitted modal id is the proof — not the 200.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Teams", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("sectionHelp-Teams", "the access-matrix widget must bind and render from a section view");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_teams_admin_pages()
    {
        // The section's negative access rule. Worth pinning at the move because the
        // controllers are now internal types in another assembly, routed through
        // SectionControllerFeatureProvider, while TeamsAdminBoardOrAdmin stayed in Shell's
        // AuthorizationPolicyExtensions (design §8's asymmetry). The shape is a 302 to
        // Program.cs's AccessDeniedPath, not a bare 403 — cookie authentication redirects an
        // authenticated-but-unauthorized request, app-wide (Cantina's finding).
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[] { "/Teams/Summary", "/Teams/Create" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"GET {url} must be denied, not sent to sign in");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_directory_renders_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the
        // neutral set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the
        // assertions stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture
        // provider returns the user's PreferredLanguage and short-circuits the rest of the
        // chain, and every Teams page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Teams", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Teams", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Calendario de cumplea");   // Birthdays_Title, accent-free run
        html.Should().Contain("Mapa de Humans");           // Map_Title
        html.Should().NotContain("Birthday Calendar");
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
