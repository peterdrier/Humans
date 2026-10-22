using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Cantina page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// and only Spanish proves the satellites ship. No view links the CSV exports, so a GET each proves their routes. With
/// no active event every page renders its no-data branch, which still carries the headings and tag helpers.
/// </remarks>
public class CantinaPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static (string Url, string Copy)[] Pages =>
    [
        ("/Cantina/Roster", "Cantina weekly roster"),   // Cantina_Roster_PageTitle
        ("/Cantina/Roster/Day", "humans on site"),      // Cantina_Day_TotalOnSite
        ("/Cantina/Dietary", "Dietary info"),           // Cantina_Dietary_PageTitle
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

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            html.Should().NotContain("Cantina_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_weekly_roster_renders_its_aggregate_cards_and_per_day_table()
    {
        // The per-day table is the section's only <partial>; _Table resolves by name across parts and a miss renders
        // nothing.
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
        // No view links the CSV writers, so nothing else would notice their routes breaking.
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
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
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
        // Only Spanish proves the satellites shipped. HtmlEncoder escapes non-ASCII ("únicas" becomes "&#xFA;nicas"),
        // so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Cantina/Roster", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
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
