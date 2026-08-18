using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders GoogleIntegration's pages through the real app — the standing form of the §15
/// step 12 check for the section's move into its own project
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// This is the only test that can catch the failure the move is most likely to produce: a
/// section outside Shell's dependency graph. <c>SectionAssemblies()</c> walks
/// <c>DependencyContext</c>, so a missing <c>ProjectReference</c> in
/// <c>Humans.Web.csproj</c> means <c>Section.Register</c> never runs, the internal
/// <c>GoogleController</c> is never discovered by <c>SectionControllerFeatureProvider</c>,
/// and all 30 routes 404 — while the solution builds and the whole suite passes
/// (G5-SECTION-TEMPLATE.md step 1, Guide's case).
/// </para>
/// <para>
/// Three failure modes are silent and get an assertion each. A <c>&lt;vc:&gt;</c> whose
/// component is not visible from the section renders as inert literal markup rather than
/// throwing — <c>&lt;vc:access-matrix&gt;</c> is back on the tag-helper form since the
/// component moved to <c>Humans.UI</c> (nobodies-collective/Humans#1056), and it binds
/// through the section's <c>@@addTagHelper *, Humans.Interfaces</c>. Note it renders <b>empty</b> on
/// <c>/Google</c>: neither <c>AccessMatrixDefinitions.Sections</c> nor <c>SectionHelpContent</c>
/// has a "Google" key, a content gap tracked separately — so there is no modal id to assert
/// here, only the absence of literal markup. A key the resx carve missed renders as its own
/// name. And the section's <c>_ViewImports</c> is what binds every tag helper, so a missing
/// line there ships broken HTML with a green build.
/// </para>
/// </remarks>
public class GoogleIntegrationPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly string[] AdminPages =
    [
        "/Google",
        "/Google/SyncSettings",
        "/Google/Sync",
        "/Google/AllGroups",
        "/Google/Accounts",
        "/Google/SyncOutbox",
        "/Google/EmailFlagViolations",
    ];

    /// <summary>
    /// The three pages that read their model out of TempData and redirect to <c>/Google</c>
    /// when there is none, so a bare GET cannot render them. They still belong here: a 302
    /// from the action and a 404 from the router are exactly the two outcomes this file exists
    /// to tell apart, and the router is what a section missing from Shell's dependency graph
    /// breaks.
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
            // A 200 is also the proof that /Google's `Component.InvokeAsync("AccessMatrix", ...)`
            // resolves: an unresolvable invoke-by-name throws rather than degrading. Its own
            // output is not assertable — AccessMatrixDefinitions has no "Google" key, so the
            // component has rendered empty since before this move.
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");
            html.Should().NotContain("-view-component", $"GET {url} has a ReSharper-rewritten vc tag");
            html.Should().NotContain("GoogleAccounts_",
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
        // The section's only localized copy is the three GoogleAccounts_* keys, and
        // Accounts.cshtml is their sole renderer. A resource set whose manifest prefix is
        // wrong degrades every string to its raw key without failing anything else, so this
        // asserts the resolved English value rather than the absence of the key.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Google/Accounts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Actions", "GoogleAccounts_Actions must resolve out of GoogleIntegrationResource");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Accounts_page_renders_the_sections_Spanish_satellite()
    {
        // The neutral set is embedded in the section assembly, so an English-only check passes
        // whether or not the satellite assemblies shipped with the RCL. Every page here is
        // [Authorize], and Program.cs's initial culture provider short-circuits on the signed-in
        // user's PreferredLanguage, so Accept-Language never reaches it — switch the way the UI
        // does (step 12, CityPlanning's case).
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var switcherPage = await (await Client.GetAsync("/Google/Accounts", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Google/Accounts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        // ASCII-only substring of "Acciones": Razor's HtmlEncoder escapes non-ASCII to numeric
        // entities, so asserting an accented resx value fails on a perfectly correct page.
        html.Should().Contain("Acciones", "the section's .es satellite must reach the host's probing path");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Google_admin_pages_are_closed_to_a_non_privileged_human()
    {
        // The move rehomed the controller into an internal type in another assembly routed by
        // SectionControllerFeatureProvider, while its policy stayed in Shell's
        // AuthorizationPolicyExtensions (step 6's asymmetry). One GET as a volunteer proves the
        // two halves still meet. Cookie authentication redirects an authenticated-but-
        // unauthorized request to Program.cs's AccessDeniedPath app-wide — not a 403.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Google/SyncSettings", ct);

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
