using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Surveys page through the real app after the section moved into
/// <c>src/Sections/Humans.Surveys</c> (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// This is the standing form of the §15 step 12 check. The two failure modes a G5 move
/// introduces both render as a <b>200 with degraded content</b>, which is why "the page loads"
/// is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c>. A missing
/// <c>@using</c> or <c>@addTagHelper</c> ships literal markup with a green build.
/// </description></item>
/// <item><description>
/// A resource key that did not make it into the carved <c>SurveysResource</c> set falls back
/// to rendering its own raw key — in every language, with no missing-key error, because the
/// fallback <em>is</em> the designed behaviour.
/// </description></item>
/// </list>
/// <para>
/// Each case therefore asserts on resolved <c>Survey_*</c> copy and on the absence of any raw
/// <c>Survey_</c> key in the response body, which catches both at once. A one-off pre/post HTML
/// diff would too, but only for the person running it; this runs on every build.
/// </para>
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

        // Resolved copy, not the key: proves SurveysResource.resx is reachable under the
        // section's manifest prefix (the .cs namespace, not the folder path — design §3).
        html.Should().Contain("Start");
        html.Should().Contain("name=\"Anonymity\"");
        html.Should().Contain("value=\"Anonymous\"");
        html.Should().NotContain("id=\"anonIdentified\"");
        html.Should().NotContain("Survey_",
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

        // The slug still resolves — the survey exists and still allows anonymous responding — so
        // this is a 200 rendering Closed.cshtml, not a 404. That view is the one whose entire
        // content is localized strings, which makes it the sharpest check on the resx carve.
        var response = await Client.GetAsync($"/Survey/{slug}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("This survey is closed");
        html.Should().NotContain("Survey_");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Public_survey_intro_renders_in_Spanish_from_the_sections_satellite_assembly()
    {
        // The non-English check the §15 step 12 HTML diff cannot do: a capture taken in English
        // passes whether or not the section's satellite assemblies shipped, because the neutral
        // set is embedded in the main assembly and the fallback is silent. This is the only
        // assertion in the file that would catch SurveysResource.es.resx failing to build into
        // a satellite, or the RCL's satellites failing to reach the host's probing path.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var slug = $"render-test-{Guid.NewGuid():N}";
        await SeedOpenPublicSurveyAsync(slug, ct, status: SurveyStatus.Closed);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Survey/{slug}");
        request.Headers.Add("Accept-Language", "es");
        var response = await Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities ("está" renders as
        // "est&#xE1;"), so the assertions stay on ASCII-only runs of the Spanish strings.
        html.Should().Contain("Encuesta no disponible");   // Survey_Closed_Title
        html.Should().Contain("Esta encuesta est");        // Survey_Closed_Heading
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
            html.Should().NotContain("Survey_", $"GET {url} rendered a raw resource key");
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
