using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders GoogleIntegration's pages through the real app.</summary>
/// <remarks>
/// A missing <c>ProjectReference</c> in <c>Humans.Web.csproj</c> leaves the section undiscovered and every route a
/// 404 while the build and the rest of the suite pass; only this catches it. The access matrix renders empty on
/// <c>/Google</c> (no section contributes a "Google" key), so only the absence of literal markup is assertable.
/// </remarks>
public class GoogleIntegrationPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly string[] AdminPages =
    [
        "/Google",
        // /Google/SyncSettings has no GET: the form lives at /Settings#google-sync.
        "/Google/Sync",
        "/Google/AllGroups",
        "/Google/Accounts",
        "/Google/SyncOutbox",
        "/Google/EmailFlagViolations",
        // A bare GET is the dry run, so it renders and writes nothing.
        "/Google/Admin/SyncHistoryMigration",
    ];

    /// <summary>
    /// These read their model from TempData and redirect without it. A 302 from the action versus a 404 from the router
    /// is the distinction this file exists for.
    /// </summary>
    private static readonly string[] TempDataBackedPages =
    [
        "/Google/SyncResults",
        "/Google/GroupSettingsResults",
        "/Google/EmailRenames",
    ];

    [HumansFact(Timeout = 300000)]
    public async Task Every_admin_page_renders_with_tag_helpers_and_view_components_bound()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in AdminPages)
        {
            // A 200 also proves /Google's invoke-by-name of AccessMatrix resolves: a miss throws.
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().NotContain("-view-component", $"GET {url} has a ReSharper-rewritten vc tag");
            html.Should().NotContain("GoogleIntegration_Accounts_",
                $"GET {url} rendered a raw resource key — the resx carve missed one, or a type bound the wrong localizer");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Every_TempData_backed_page_routes_to_its_action()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in TempDataBackedPages)
        {
            var response = await Client.GetAsync(url, ct);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect,
                $"GET {url} must reach the action and be redirected by it, not 404 from the router");
            response.Headers.Location?.ToString().Should().Contain("/Google",
                $"GET {url} must redirect to the section's own index");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Accounts_page_renders_the_sections_own_resource_set()
    {
        // A wrong manifest prefix degrades every string to its raw key without failing anything else, so assert the
        // resolved English value.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Google/Accounts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Actions", "GoogleIntegration_Accounts_Actions must resolve out of GoogleIntegrationResource");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Accounts_page_renders_the_sections_Spanish_satellite()
    {
        // Only Spanish proves the satellites shipped. Accept-Language doesn't reach a signed-in user, so switch the way
        // the UI does.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var switcherPage = await (await Client.GetAsync("/Google/Accounts", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Google/Accounts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        // HtmlEncoder escapes non-ASCII, so an accented value would fail on a correct page.
        html.Should().Contain("Acciones", "the section's .es satellite must reach the host's probing path");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Google_admin_pages_are_closed_to_a_non_privileged_human()
    {
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Google/AllGroups", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "AdminOnly in Shell must still gate the section's controller");
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
