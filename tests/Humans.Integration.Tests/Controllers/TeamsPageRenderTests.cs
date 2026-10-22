using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Teams page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// and only Spanish proves the satellites ship. Five keys stay in SharedResource, so the sweep covers carved and shared
/// prefixes. The fixture is the dev personas only: <c>CachingTeamService</c> warms at startup, so a direct context seed
/// would be invisible anyway.
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

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            foreach (var prefix in CarvedPrefixes)
            {
                html.Should().NotContain(prefix, $"GET {url} rendered a raw {prefix}* resource key");
            }

            // Shared keys the section still reads through SharedLocalizer.
            html.Should().NotContain("Common_", $"GET {url} rendered a raw shared key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_directory_renders_the_access_matrix_tag_helper()
    {
        // An unbound <vc:> ships as inert markup, so the emitted modal id is the proof, not the 200.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Teams", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("sectionHelp-Teams", "the access-matrix widget must bind and render from a section view");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_teams_admin_pages()
    {
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
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
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Teams", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
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
