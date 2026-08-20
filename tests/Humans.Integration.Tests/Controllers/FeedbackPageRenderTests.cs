using System.Net;
using AwesomeAssertions;
using Humans.Feedback.Data;
using Humans.Feedback.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Feedback page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Feedback</c>
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
/// <c>&lt;vc:human&gt;</c> on the detail panel is the one Feedback uses.
/// </description></item>
/// <item><description>
/// The resx carve moved all 31 <c>Feedback_*</c> / <c>Enum_Feedback*</c> keys out of
/// <c>SharedResource</c>. A key the carve missed, or a call site bound to the wrong set,
/// renders the raw key — in every language, with no error. The Spanish request is the only
/// thing that proves the section RCL's satellite assemblies reach the host's probing path
/// at all.
/// </description></item>
/// </list>
/// <para>
/// Feedback ships no <c>wwwroot/</c>, so there is no static-asset half here. The
/// <c>Email_FeedbackResponse_*</c> keys deliberately stayed in <c>SharedResource</c> — Base's
/// <c>EmailRenderer</c> owns that email — so they are outside this check.
/// </para>
/// </remarks>
public class FeedbackPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// Writes one historical report and one admin reply straight through the section's own
    /// DbContext — Feedback has no creation path any more (nobodies-collective/Humans#977) and
    /// there is no §15 caching decorator in front of the service, so the context write is what
    /// the page reads. Without a row, <c>/Feedback</c> renders its empty-list view and most of
    /// the carved keys — the whole point of the check — never render. Each integration test
    /// class owns its database (nobodies-collective/Humans#983), so this disturbs nothing.
    /// </summary>
    private async Task<Guid> SeedReportAsync(CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();

        var reportId = Guid.NewGuid();
        db.FeedbackReports.Add(new FeedbackReport
        {
            Id = reportId,
            UserId = Guid.NewGuid(),
            Category = FeedbackCategory.Bug,
            Description = "The rota page scrolls sideways on a phone",
            PageUrl = "/Shifts/Rota",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.FeedbackMessages.Add(new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            SenderUserId = Guid.NewGuid(),
            Content = "Thanks, reproduced.",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return reportId;
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_feedback_index_renders_without_raw_resource_keys_or_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Feedback", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        // The fallback for a key the carve missed is the key itself.
        html.Should().NotContain("Feedback_", "GET /Feedback rendered a raw resource key");
        html.Should().NotContain("Enum_Feedback", "GET /Feedback rendered a raw enum resource key");

        // Resolved copy from the section's own set, plus an Enum_FeedbackStatus_* value —
        // those are read through EnumLocalizationExtensions, not a literal Localizer[…] call.
        html.Should().Contain("Select a report to view details");
        html.Should().Contain("Open");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_feedback_detail_partial_renders_its_thread_and_the_human_component()
    {
        // /Feedback/{id} only returns the partial for the page's own AJAX call; a plain GET
        // redirects to the list with ?selected=. The partial is where <vc:human> lives, so the
        // XHR path is the one that proves the tag helper bound.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var reportId = await SeedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Feedback/{reportId}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().NotContain("Feedback_", "the detail partial rendered a raw resource key");
        html.Should().Contain("Conversation");
        html.Should().Contain("Thanks, reproduced.");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Feedback_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Feedback page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Feedback", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Feedback", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Comentarios");                  // Feedback_PageTitle
        html.Should().Contain("Todos los estados");            // Feedback_AllStatuses
        html.Should().Contain("Selecciona un informe");        // Feedback_SelectReport
        html.Should().NotContain("Select a report to view details");
        html.Should().NotContain("Feedback_");
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
