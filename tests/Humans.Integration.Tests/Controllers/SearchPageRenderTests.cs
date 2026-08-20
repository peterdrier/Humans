using System.Net;
using AwesomeAssertions;
using Humans.Base.Data;
using Humans.Integration.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the global <c>/Search</c> page through the real app, as the standing form of the
/// §15 step 12 check for the section's move into <c>src/Sections/Humans.Search</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// Search is one page, and every failure mode the move introduces renders it as a
/// <b>200 with degraded content</b>, so "the page loads" is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// The section is only reachable at all because <c>Humans.Web.csproj</c> references it —
/// <c>SectionDiscoveryExtensions</c> walks the dependency graph, so a missing reference builds
/// green, passes the whole unit suite and 404s every page. These GETs are that check.
/// </description></item>
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c>. The Humans
/// bucket renders one <c>&lt;vc:user-search-result&gt;</c> per hit — Users' own component,
/// keyed by user id (nobodies-collective/Humans#1062) — and that element binds only through
/// this section's <c>@addTagHelper *, Humans.Users</c>. Drop that line and every row ships as
/// inert literal markup on a green 200.
/// </description></item>
/// <item><description>
/// The resx carve moved 12 of the 17 <c>Search_*</c> keys into <c>SearchResource</c> and left
/// five behind — <c>Search_MinChars</c> is read by this page *and* by Shell's
/// <c>/Profile/Search</c>, so the section binds a second localizer for it. A call site on the
/// wrong localizer renders the raw key, in every language, with no error.
/// </description></item>
/// </list>
/// <para>
/// The fixture is the seeded dev personas and nothing else: every persona's burner name is
/// "Dev &lt;role&gt;", so <c>q=Dev</c> fills the Humans bucket without touching a section this
/// test does not own. The other four buckets stay empty, which is fine — they render through
/// <c>_GlobalSearchSection</c>, whose markup carries no resource key at all.
/// </para>
/// </remarks>
public class SearchPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task The_search_page_renders_its_own_copy_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in new[] { "/Search", "/Search?q=a", "/Search?q=Dev", "/Search?q=zzqqxx" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            // The fallback for a key the carve missed — or for a call site left on the wrong
            // localizer — is the key itself.
            html.Should().NotContain("Search_", $"GET {url} rendered a raw resource key");

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_empty_and_short_query_branches_render_from_both_localizers()
    {
        // The hint is the section's own set; the min-chars message stayed in SharedResource
        // because Shell's /Profile/Search renders it too, and carving it would have split that
        // page's four-key message set. Both branches on one page is the only place the two
        // bindings can be told apart.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var empty = await (await Client.GetAsync("/Search", ct)).Content.ReadAsStringAsync(ct);
        empty.Should().Contain("Type at least 2 characters to search");  // Search_GlobalHint*

        var tooShort = await (await Client.GetAsync("/Search?q=a", ct)).Content.ReadAsStringAsync(ct);
        tooShort.Should().Contain("Enter at least 2 characters to search."); // Search_MinChars
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_matching_query_renders_the_chips_and_the_humans_bucket()
    {
        // The rows are Users' <vc:user-search-result>, bound through this section's
        // @addTagHelper. An unbound one is a 200 with the element as literal markup, so the
        // persona's name and the component's own match badge are what prove it ran.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Search?q=Dev", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Dev Admin", because: "the Humans bucket must render its rows");
        html.Should().Contain("Matched in",
            because: "Search_MatchedIn is written by <vc:user-search-result>'s own markup and by "
                   + "nothing else on this page");
        html.Should().Contain(">Humans<", because: "the per-type filter chips must render");
        html.Should().Contain("Camps", because: "Search_FilterCamps must resolve");
        html.Should().Contain("Shifts", because: "Search_FilterShifts must resolve");
    }

    /// <summary>
    /// The Humans bucket renders one component instance per hit
    /// (nobodies-collective/Humans#1062), so the shape to prove absent is a query per row.
    /// </summary>
    /// <remarks>
    /// Every read on the path — <c>SearchUsersAsync</c> and the two <c>&lt;vc:human&gt;</c>
    /// lookups inside each row — goes through <c>CachingUserService</c>'s <c>UserInfo</c>
    /// dictionary, so a warm cache costs nothing per row and the count is flat in the number
    /// of results. A future row that reaches past the cache turns this red.
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_humans_bucket_costs_no_query_per_row()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        using (var warmScope = Factory.Services.CreateScope())
        {
            await warmScope.ServiceProvider
                .GetRequiredService<IUserServiceRead>()
                .GetAllUserInfosAsync(ct);
        }

        // Pay the one-time auth/culture/claims costs before measuring the reload itself.
        (await Client.GetAsync("/Search?q=Dev", ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var queryStats = scope.ServiceProvider.GetRequiredService<QueryStatistics>();
        queryStats.Reset();

        var response = await Client.GetAsync("/Search?q=Dev", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Dev Admin", because: "an empty bucket would make the count vacuous");

        var personReads = queryStats.GetSnapshot()
            .Where(e => string.Equals(e.Operation, "SELECT", StringComparison.Ordinal)
                     && e.Table is "users" or "profiles" or "user_emails" or "contact_fields")
            .Sum(e => e.Count);

        personReads.Should().Be(0,
            because: "one <vc:user-search-result> per row must not mean one query per row — "
                   + "every read is a UserInfo cache hit");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_query_with_no_hits_renders_the_no_results_message()
    {
        // Search_GlobalNoResults is the one carved key rendered through HtmlContentBuilder
        // .AppendFormat rather than a plain @Localizer[…], so it is the call site a carve
        // mistake is most likely to survive on.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Search?q=zzqqxx", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("No results for");
        html.Should().Contain("<strong>zzqqxx</strong>");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_type_filter_keeps_the_page_rendering()
    {
        // filter binds SearchResultType, which turned internal at the move. Model binding on an
        // internal enum parameter of an internal controller is exactly the kind of thing that
        // fails at request time and nowhere else.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Search?q=Dev&filter=Human", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Dev Admin");
        html.Should().NotContain("Search_");
    }

    [HumansFact(Timeout = 120000)]
    public async Task An_anonymous_visitor_cannot_reach_search()
    {
        // The section's one negative access rule. Worth pinning at the move because the
        // controller is now an internal type in another assembly routed through
        // SectionControllerFeatureProvider, while [Authorize]'s cookie handler stays in Shell.
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/Search?q=Dev", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.PathAndQuery.Should().StartWith("/Account/Login");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shells_nav_still_links_to_the_moved_controller()
    {
        // _Layout's magnifying glass is <a asp-controller="Search" asp-action="Index">. The
        // anchor tag helper resolves that against the route table, and a controller it cannot
        // find makes it emit the anchor with **no href at all** — a 200 with a dead link that
        // neither the suite nor an HTML diff notices (AdminNavTree's failure mode, on Shell's
        // main nav). Any authenticated page renders the layout.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var html = await (await Client.GetAsync("/Teams", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("href=\"/Search\"",
            because: "the nav link must still resolve to the section's controller");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Search_renders_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent. Accept-Language does
        // not reach a signed-in page (Program.cs's initial culture provider returns the user's
        // PreferredLanguage and short-circuits), and /Search is [Authorize], so switch the
        // language the way the UI does. Assertions stay on ASCII-only runs because Razor's
        // HtmlEncoder escapes non-ASCII to numeric entities.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var switcherPage = await (await Client.GetAsync("/Search", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Search?q=Dev", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Buscar");    // Search_GlobalTitle
        html.Should().Contain("Barrios");   // Search_FilterCamps
        html.Should().Contain("Turnos");    // Search_FilterShifts
        html.Should().NotContain("Search_");
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
