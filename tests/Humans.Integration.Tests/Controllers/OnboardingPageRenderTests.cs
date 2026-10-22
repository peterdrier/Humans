using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Onboarding page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, and a mis-bound key renders as its
/// name. Two <c>OnboardingResource</c> call sites live outside the section; <c>OnboardingLocalizerBindingTests</c> is
/// the static half covering every call site. The progress banner is an internal view component on every
/// authenticated page, so a discovery miss 500s the whole app.
/// </remarks>
public class OnboardingPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>All 106 keys carry this prefix, so any literal hit is a key name shown to a member.</summary>
    private static readonly string[] CarvedPrefixes = ["Onboarding_"];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("-view-component", $"{what} has a renamed view-component element");
        foreach (var prefix in CarvedPrefixes)
        {
            html.Should().NotContain(prefix, $"{what} rendered a raw {prefix}* resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_review_queue_renders_its_copy_and_the_shell_owned_widgets()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/OnboardingReview", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /OnboardingReview");

        html.Should().Contain("Onboarding Review Queue");           // Onboarding_ReviewTitle
        html.Should().Contain("No humans are waiting for review.");  // Onboarding_ReviewNoPending
        // The modal id only the component emits; the bare section key would also match the page title.
        html.Should().Contain("sectionHelp-OnboardingReview",
            "<vc:access-matrix> must bind through @addTagHelper *, Humans.Base");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_review_detail_page_renders_for_a_seeded_human()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // The detail route doesn't require the user to be queued, so the admin's own page covers <vc:human>, the
        // ProfileCard invocation and the whole label set.
        var response = await Client.GetAsync($"/OnboardingReview/{adminId}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /OnboardingReview/{id}");

        html.Should().Contain("Legal Name");        // Onboarding_ReviewLegalName
        html.Should().Contain("Onboarding Review Queue");  // Onboarding_ReviewTitle, on the breadcrumb
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_widget_name_step_renders_its_form()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/OnboardingWidget/Names", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /OnboardingWidget/Names");

        html.Should().Contain("Continue");   // Onboarding_Continue
        // The [Display] labels stay in SharedResource: MVC's DataAnnotationLocalizerProvider is bound to that set and
        // can't see a section's.
        html.Should().Contain("Burner name");
        html.Should().NotContain("Onboarding_BurnerNameLabel");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_widget_shift_step_renders_through_shells_rota_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/OnboardingWidget/Shifts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /OnboardingWidget/Shifts");

        // The rota tables come from Shell's OnboardingShiftsList, invoked by name; an unresolvable InvokeAsync throws,
        // so the 200 is the proof.
        html.Should().Contain("Pick a shift");   // Onboarding_ShiftsTitle
        html.Should().Contain("Not right now");  // Onboarding_ShiftsNotNow
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_shell_page_renders_the_sections_internal_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // The banner is internal and on every authenticated page; without SectionViewComponentFeatureProvider this 500s.
        var response = await Client.GetAsync("/Teams", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Teams as a fully-onboarded persona");
    }

    /// <summary>
    /// For a fully onboarded user the banner is hidden, so a "no raw keys" check inspects an empty string; two
    /// misbound keys shipped that way. Asserting the copy is present is what makes the no-key check mean something.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_progress_banner_renders_its_copy_to_a_human_still_onboarding()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsMidOnboardingAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Teams", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Continue setup",
            "the banner must render for a human who has not finished onboarding — if this fails "
            + "the fixture is complete again and the key assertion below is vacuous");
        AssertRenderedCleanly(html, "GET /Teams as a mid-onboarding persona");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Onboarding_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var page = await (await Client.GetAsync("/OnboardingWidget/Names", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(page);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/OnboardingWidget/Names", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /OnboardingWidget/Names (es)");

        html.Should().Contain("Continuar");                                    // Onboarding_Continue
        html.Should().Contain("Dos pasos cortos antes de que elijas un turno"); // Onboarding_NamesSubtitle
        html.Should().NotContain("Two short steps");
    }

    /// <summary>
    /// Cookie auth redirects an authenticated-but-unauthorized request to <c>AccessDeniedPath</c>: a 302, not a 403.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task A_plain_volunteer_is_bounced_off_the_review_queue()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/OnboardingReview", ct);
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
            because: "the queue must be denied, not sent to sign in");
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
