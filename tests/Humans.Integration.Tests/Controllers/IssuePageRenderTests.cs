using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Issues page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// and only Spanish proves the satellites ship. The help widget is Shell chrome rendering the section's
/// <c>_IssueWidgetModal</c> by name, so every page here also checks the modal's copy.
/// </remarks>
public class IssuePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string SeededTitle = "Rota page scrolls sideways on a phone";

    /// <summary>
    /// Written straight to the context: no caching decorator fronts <c>IssuesService</c>. Without a row the list renders
    /// empty and most keys never show.
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
            Section = "Shifts",
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

        html.Should().NotContain("Issue_", "GET /Issues rendered a raw resource key");
        html.Should().NotContain("Enum_Issue", "GET /Issues rendered a raw enum resource key");

        html.Should().Contain("Select an issue from the list to view details.");
        html.Should().Contain(SeededTitle);

        html.Should().Contain("File an issue", "the widget modal partial did not resolve its copy");
        html.Should().Contain("id=\"issuesWidgetModal\"");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_new_issue_form_renders_its_area_and_category_selects()
    {
        // Category keys render through EnumLocalizationExtensions, not a Localizer[…] call, so a Localizer grep misses
        // them.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Issues/New", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("Issue_", "GET /Issues/New rendered a raw resource key");
        html.Should().NotContain("Enum_Issue", "GET /Issues/New rendered a raw enum resource key");

        html.Should().Contain("Tips for a good issue");
        html.Should().Contain("Shifts &amp; volunteering", "the Area select lost AreaLabelMap");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_issue_detail_partial_renders_its_thread_and_the_human_component()
    {
        // A plain GET redirects to the list; only the page's XHR gets the partial that holds <vc:human>.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var issueId = await SeedIssueAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Issues/{issueId}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("Issue_", "the detail partial rendered a raw resource key");
        html.Should().Contain("Conversation");
        html.Should().Contain("Thanks, reproduced.");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Issue_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedIssueAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Issues", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
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
