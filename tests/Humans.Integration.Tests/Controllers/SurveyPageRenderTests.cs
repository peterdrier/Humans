using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Microsoft.Extensions.DependencyInjection;
using Humans.Surveys.Contracts;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Surveys page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: a missing <c>@using</c> or <c>@addTagHelper</c> ships literal
/// markup, and a key missing from <c>SurveysResource</c> renders as its own name in every language. Asserting resolved
/// <c>Surveys_*</c> copy and no raw <c>Surveys_</c> key catches both.
/// </remarks>
public class SurveyPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60000)]
    public async Task Public_survey_intro_renders_localized_copy_from_the_sections_own_resource_set()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var slug = $"render-test-{Guid.NewGuid():N}";
        await SeedOpenPublicSurveyAsync(slug, ct);

        var response = await Client.GetAsync($"/Survey/{slug}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Resolved copy proves the resx is reachable under the manifest prefix (the .cs namespace, not the folder path).
        html.Should().Contain("Start");
        html.Should().Contain("name=\"Anonymity\"");
        html.Should().Contain("value=\"Anonymous\"");
        html.Should().NotContain("id=\"anonIdentified\"");
        html.Should().NotContain("Surveys_",
            because: "an unresolved key renders as its own literal name and survives an English-only "
                   + "eyeball check of the page");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Logged_in_public_survey_intro_offers_all_three_representation_choices()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var slug = $"render-test-{Guid.NewGuid():N}";
        await SeedOpenPublicSurveyAsync(slug, ct);

        var response = await Client.GetAsync($"/Survey/{slug}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("id=\"anonIdentified\"");
        html.Should().Contain("value=\"Identified\"");
        html.Should().Contain("value=\"CompletionTracked\"");
        html.Should().Contain("value=\"Anonymous\"");
        html.Should().Contain("id=\"anonIdentified\" value=\"Identified\" checked");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Public_survey_intro_renders_sanitized_markdown_and_preserves_line_breaks()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var slug = $"render-test-{Guid.NewGuid():N}";
        await SeedOpenPublicSurveyAsync(
            slug,
            ct,
            intro: "First line\nSecond line\n\n**Important**\n\n<script>alert('unsafe')</script>");

        var html = await (await Client.GetAsync($"/Survey/{slug}", ct))
            .Content.ReadAsStringAsync(ct);

        html.Should().Contain("First line<br>");
        html.Should().Contain("<strong>Important</strong>");
        html.Should().NotContain("alert('unsafe')");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Closed_survey_renders_the_closed_page_rather_than_the_wizard()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var slug = $"render-test-{Guid.NewGuid():N}";
        await SeedOpenPublicSurveyAsync(slug, ct, status: SurveyStatus.Closed);

        // The survey still exists, so this is a 200 rendering Closed.cshtml, not a 404. That view is all localized
        // strings, the sharpest check on the resx.
        var response = await Client.GetAsync($"/Survey/{slug}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("This survey is closed");
        html.Should().NotContain("Surveys_");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Public_survey_intro_renders_in_Spanish_from_the_sections_satellite_assembly()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites reach the host's probing path.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var slug = $"render-test-{Guid.NewGuid():N}";
        await SeedOpenPublicSurveyAsync(slug, ct, status: SurveyStatus.Closed);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Survey/{slug}");
        request.Headers.Add("Accept-Language", "es");
        var response = await Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // HtmlEncoder escapes non-ASCII ("está" becomes "est&#xE1;"), so the checks stay on ASCII runs.
        html.Should().Contain("Encuesta no disponible");   // Surveys_Closed_Title
        html.Should().Contain("Esta encuesta est");        // Surveys_Closed_Heading
        html.Should().NotContain("This survey is closed");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Admin_survey_index_and_results_render()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var slug = $"render-test-{Guid.NewGuid():N}";
        var surveyId = await SeedOpenPublicSurveyAsync(slug, ct);

        foreach (var url in new[] { "/Survey/Admin", $"/Survey/Admin/Edit/{surveyId}", $"/Survey/Admin/Send/{surveyId}", $"/Survey/Admin/Results/{surveyId}" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().NotContain("Surveys_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task Admin_survey_builder_renders_shared_markdown_editors_and_dynamic_question_controls()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var slug = $"render-test-{Guid.NewGuid():N}";
        var surveyId = await SeedOpenPublicSurveyAsync(slug, ct);

        var html = await (await Client.GetAsync($"/Survey/Admin/Edit/{surveyId}", ct))
            .Content.ReadAsStringAsync(ct);

        html.Should().Contain("name=\"Intro[en]\"");
        html.Should().Contain("name=\"InvitationEmailMessage[en]\"");
        html.Should().Contain("easymde@2.21.0");
        html.Should().Contain("new EasyMDE");
        html.Should().Contain("data-humans-markdown-editor=\"true\"");
        html.Should().Contain("window.HumansMarkdownEditor");
        html.Should().Contain("window.HumansMarkdownEditor?.init(card)");
        html.Should().Contain("move-question-up");
        html.Should().Contain("move-question-down");
        html.Should().Contain("add-question-after");
        html.Should().NotContain("<markdown-editor");
    }

    private async Task<Guid> SeedOpenPublicSurveyAsync(
        string slug,
        CancellationToken ct,
        SurveyStatus status = SurveyStatus.Open,
        string intro = "Intro copy")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();

        var survey = new Survey
        {
            Id = Guid.NewGuid(),
            Title = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Render test survey" }),
            Intro = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = intro }),
            ThankYou = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Thanks" }),
            DefaultCulture = "en",
            AllowAnonymous = true,
            Status = status,
            PublicSlug = slug,
        };

        db.Surveys.Add(survey);
        await db.SaveChangesAsync(ct);
        return survey.Id;
    }
}
