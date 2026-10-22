using System.Net;
using AwesomeAssertions;
using Humans.Feedback.Data;
using Humans.Feedback.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Feedback.Contracts;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Feedback page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: an unbound <c>&lt;vc:human&gt;</c> ships as markup, a mis-bound key
/// renders as its name, and only Spanish proves the satellites ship. <c>Feedback_Email_*</c> keys are email copy, not
/// page copy.
/// </remarks>
public class FeedbackPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// Written straight to the context: Feedback has no creation path and no caching decorator. Without a row the page
    /// renders its empty list and most keys never show.
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

        html.Should().NotContain("Feedback_", "GET /Feedback rendered a raw resource key");
        html.Should().NotContain("Enum_Feedback", "GET /Feedback rendered a raw enum resource key");

        // Enum_FeedbackStatus_* values render through EnumLocalizationExtensions, not a Localizer[…] call.
        html.Should().Contain("Select a report to view details");
        html.Should().Contain("Open");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_feedback_detail_partial_renders_its_thread_and_the_human_component()
    {
        // A plain GET redirects to the list; only the page's XHR gets the partial that holds <vc:human>.
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
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedReportAsync(ct);
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Feedback", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
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
