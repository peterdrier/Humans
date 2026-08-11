using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Issues page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Issues</c>
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
/// <c>&lt;vc:human&gt;</c> is on both the index rows and the detail panel.
/// </description></item>
/// <item><description>
/// The resx carve moved all 78 <c>Issue_*</c> / <c>Enum_IssueCategory_*</c> keys out of
/// <c>SharedResource</c>. A key the carve missed, or a call site bound to the wrong set,
/// renders the raw key — in every language, with no error. The Spanish request is the only
/// thing that proves the section RCL's satellite assemblies reach the host's probing path.
/// </description></item>
/// <item><description>
/// Eleven of those keys are rendered by the floating help widget, which is Shell chrome. Its
/// issue-submission modal moved into this section as <c>Views/Shared/_IssueWidgetModal.cshtml</c>
/// and Shell reaches it by name across application parts; a partial that fails to resolve
/// throws, but one that resolves against the wrong resource set renders raw keys. Every
/// assertion below runs against a page that carries the widget, so the modal's own copy is
/// checked on each of them.
/// </description></item>
/// </list>
/// <para>
/// Issues ships no <c>wwwroot/</c>, so there is no static-asset half here.
/// </para>
/// </remarks>
public class IssuePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string SeededTitle = "Rota page scrolls sideways on a phone";

    /// <summary>
    /// Writes one issue and one comment straight through the section's own DbContext. There is
    /// no §15 caching decorator in front of <c>IssuesService</c> — it caches only the nav-badge
    /// count — so the context write is what the list and detail reads see. Without a row,
    /// <c>/Issues</c> renders its empty-list view and most of the carved keys never render.
    /// Each integration test class owns its database (nobodies-collective/Humans#983).
    /// </summary>
    private async Task<Guid> SeedIssueAsync(CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IssuesDbContext>();

        var issueId = Guid.NewGuid();
        db.Issues.Add(new Issue
        {
            Id = issueId,
            ReporterUserId = Guid.NewGuid(),
            Section = IssueSectionRouting.Shifts,
            Category = IssueCategory.Bug,
            Title = SeededTitle,
            Description = "The rota table overflows the viewport below 400px.",
            PageUrl = "/Shifts/Rota",
            Status = IssueStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.IssueComments.Add(new IssueComment
        {
            Id = Guid.NewGuid(),
            IssueId = issueId,
            SenderUserId = Guid.NewGuid(),
            Content = "Thanks, reproduced.",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return issueId;
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_issues_index_renders_without_raw_resource_keys_or_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedIssueAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Issues", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        // A view component element the section's _ViewImports failed to bind renders as
        // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
        html.Should().NotContain("<vc:", "GET /Issues left a view-component tag unrendered");

        // The fallback for a key the carve missed is the key itself.
        html.Should().NotContain("Issue_", "GET /Issues rendered a raw resource key");
        html.Should().NotContain("Enum_Issue", "GET /Issues rendered a raw enum resource key");

        html.Should().Contain("Select an issue from the list to view details.");
        html.Should().Contain(SeededTitle);

        // The help widget's modal is Shell chrome rendering the section's partial.
        html.Should().Contain("File an issue", "the widget modal partial did not resolve its copy");
        html.Should().Contain("id=\"issuesWidgetModal\"");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_new_issue_form_renders_its_area_and_category_selects()
    {
        // The category select is the one place Enum_IssueCategory_* keys render — they go
        // through EnumLocalizationExtensions, not a literal Localizer[…] call, so a carve that
        // dropped them would not show up in the Localizer grep.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Issues/New", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("<vc:");
        html.Should().NotContain("Issue_", "GET /Issues/New rendered a raw resource key");
        html.Should().NotContain("Enum_Issue", "GET /Issues/New rendered a raw enum resource key");

        html.Should().Contain("Tips for a good issue");
        html.Should().Contain("Shifts &amp; volunteering", "the Area select lost AreaLabelMap");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_issue_detail_partial_renders_its_thread_and_the_human_component()
    {
        // GET /Issues/{id} only returns the partial for the page's own AJAX call; a plain GET
        // redirects to the list with ?selected=. The partial is where <vc:human> and most of
        // the detail copy live, so the XHR path is the one that proves the tag helper bound.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var issueId = await SeedIssueAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Issues/{issueId}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("<vc:", "the detail partial left <vc:human> unrendered");
        html.Should().NotContain("Issue_", "the detail partial rendered a raw resource key");
        html.Should().Contain("Conversation");
        html.Should().Contain("Thanks, reproduced.");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Issue_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedIssueAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Issues page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Issues", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Issues", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Incidencias");                                   // Issue_PageTitle
        html.Should().Contain("Selecciona una incidencia de la lista");         // Issue_SelectOne
        html.Should().Contain("Reportar una incidencia");                       // Issue_FileAnIssue (widget modal)
        html.Should().NotContain("Select an issue from the list to view details.");
        html.Should().NotContain("Issue_");
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
