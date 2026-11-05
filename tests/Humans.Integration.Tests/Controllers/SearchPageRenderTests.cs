using System.Net;
using AwesomeAssertions;
using Humans.Base.Data;
using Humans.Integration.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders the global <c>/Search</c> page through the real app.</summary>
/// <remarks>
/// <para>
/// Failures render as a 200 with degraded content. The section is reachable only because <c>Humans.Web.csproj</c>
/// references it; a missing reference builds green and 404s. Each Humans hit is Users'
/// <c>&lt;vc:user-search-result&gt;</c>, bound only by this section's <c>@addTagHelper *, Humans.Users</c>.
/// <c>Search_MinChars</c> stays in SharedResource because Shell's <c>/Profile/Search</c> reads it too.
/// </para>
/// <para>
/// Every dev persona's burner name is "Dev &lt;role&gt;", so <c>q=Dev</c> fills the Humans bucket with no other fixture.
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

            html.Should().NotContain("Search_", $"GET {url} rendered a raw resource key");

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_empty_and_short_query_branches_render_from_both_localizers()
    {
        // The hint is the section's set and the min-chars message SharedResource's; only this page shows both.
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
        // An unbound row is literal markup on a 200, so the persona's name and the match badge prove it ran.
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
    /// One component per hit, so the shape to prove absent is a query per row. Every read goes through
    /// <c>CachingUserService</c>, so the count is flat; a row that reaches past the cache turns this red.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_humans_bucket_costs_no_query_per_row()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        using (var warmScope = Factory.Services.CreateScope())
        {
            await warmScope.ServiceProvider
                .GetRequiredService<IUserService>()
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
        // The one key rendered through HtmlContentBuilder.AppendFormat rather than @Localizer, so a binding mistake is
        // likeliest to survive here.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Search?q=zzqqxx", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("No results for");
        html.Should().Contain("<strong>zzqqxx</strong>");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_type_filter_keeps_the_page_rendering()
    {
        // Binding an internal enum on an internal controller can fail at request time and nowhere else.
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
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/Search?q=Dev", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.PathAndQuery.Should().StartWith("/Account/Login");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shells_nav_still_links_to_the_moved_controller()
    {
        // _Layout's search link uses asp-controller/asp-action; a controller the tag helper can't find yields an anchor
        // with no href, a dead link on a 200.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var html = await (await Client.GetAsync("/Teams", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("href=\"/Search\"",
            because: "the nav link must still resolve to the section's controller");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Search_renders_in_spanish_from_the_sections_satellite_assemblies()
    {
        // Only Spanish proves the satellites shipped. Accept-Language doesn't reach a signed-in user, so switch the way
        // the UI does. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var switcherPage = await (await Client.GetAsync("/Search", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
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
