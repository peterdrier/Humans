using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Onboarding page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Onboarding</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>:</para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build.
/// </description></item>
/// <item><description>
/// The resx carve moved the <c>Onboarding*</c> prefixes out of <c>SharedResource</c>. A key the
/// carve missed, or a call site left bound to the wrong set, renders the raw key in every language
/// with no error — and two of those call sites are <b>outside</b> the section (Shifts'
/// <c>ShiftsController</c> and Governance's board-voting detail page both bind
/// <c>IStringLocalizer&lt;OnboardingResource&gt;</c> now). This class only sees the pages a fixture
/// can reach and only the three carved prefixes; <c>OnboardingLocalizerBindingTests</c> is the
/// static, every-call-site, every-prefix half of the same guard.
/// </description></item>
/// <item><description>
/// The review pages' <c>&lt;vc:access-matrix&gt;</c> binds through <c>@@addTagHelper *, Humans.Base</c>
/// since the component lives there; <c>&lt;vc:profile-card&gt;</c> is Users' and is a
/// <c>Component.InvokeAsync</c> call. An unresolvable invocation throws; a stray
/// <c>&lt;vc:&gt;</c> renders as inert markup and only the <c>NotContain</c> assertion catches it.
/// </description></item>
/// <item><description>
/// The progress banner moved <i>into</i> the section, so it is an <c>internal</c> view component
/// discovered only by Shell's <c>SectionViewComponentFeatureProvider</c> — and <c>SectionChrome</c>
/// puts it in the <c>AboveContent</c> slot of every authenticated page, so a miss 500s the whole app.
/// </description></item>
/// </list>
/// <para>Onboarding ships no <c>wwwroot/</c>, so there is no static-asset half here.</para>
/// </remarks>
public class OnboardingPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// Every prefix in <c>OnboardingResource</c> — since the 2026-08-23 rename to satisfy
    /// <c>resource-key-prefix</c>, that is the single prefix <c>Onboarding_</c>, which all 106
    /// keys carry. A page rendering any of it literally is showing a key name to a human.
    /// </summary>
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
        // AccessMatrix lives in Humans.Base (nobodies-collective/Humans#1056), so the call site
        // is <vc:access-matrix section="OnboardingReview" />. An unbound <vc:> ships as inert
        // literal markup with a green build, so assert the modal id the component emits — the
        // bare section key would also match the page title and prove nothing.
        html.Should().Contain("sectionHelp-OnboardingReview",
            "<vc:access-matrix> must bind through @addTagHelper *, Humans.Base");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_review_detail_page_renders_for_a_seeded_human()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // The detail route is by user id and does not require the human to be in the queue —
        // rendering it for the signed-in admin exercises <vc:human>, the ProfileCard invocation
        // and the whole OnboardingReview_* label set on one page.
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
        // The three [Display] labels deliberately stayed in SharedResource: MVC's global
        // DataAnnotationLocalizerProvider is bound to that set and cannot see a section's, so a
        // key moved out of it renders as its own name on this form and nowhere else.
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

        // The pills and the skip button are the section's copy; the rota tables below them come
        // from Shell's OnboardingShiftsList component, invoked by name because
        // ShiftBrowseViewModel and ShiftBrowseMapper are Shifts' presentation and live in
        // Humans.Shifts. An unresolvable Component.InvokeAsync throws, so a 200 here is the proof.
        html.Should().Contain("Pick a shift");   // Onboarding_ShiftsTitle
        html.Should().Contain("Not right now");  // Onboarding_ShiftsNotNow
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_shell_page_renders_the_sections_internal_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // OnboardingProgressBannerViewComponent is internal to Humans.Onboarding and is contributed
        // to every authenticated page's AboveContent slot by SectionChrome. Without
        // SectionViewComponentFeatureProvider the invocation throws and this 500s — which is the
        // loud half of the pair; the silent half is the <vc:> assertion above.
        var response = await Client.GetAsync("/Teams", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Teams as a fully-onboarded persona");
    }

    /// <summary>
    /// The same banner, against a persona it actually renders for.
    /// </summary>
    /// <remarks>
    /// The test above signs in fully onboarded, so the banner returns <c>Show: false</c> and a
    /// "no raw keys" assertion there inspects an empty string — it cannot fail, which is how
    /// two misbound banner keys reached production. This one uses the mid-onboarding persona, so
    /// the banner is really on the page: it asserts the copy is present *and* that no key name
    /// leaked, and the first of those is what makes the second mean something.
    /// </remarks>
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
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent. Razor's default
        // HtmlEncoder escapes non-ASCII to numeric entities, so the assertions stay on ASCII-only
        // runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and every
        // widget page is [Authorize]. Switch the language the way the UI does.
        var page = await (await Client.GetAsync("/OnboardingWidget/Names", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(page);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
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
    /// The section's negative access rule: the review queue is <c>ReviewQueueAccess</c>, and the
    /// move rehomed its controller into an internal type in another assembly routed by
    /// <c>SectionControllerFeatureProvider</c> while the policy stayed in Shell's
    /// <c>AuthorizationPolicyExtensions</c>. Cookie authentication redirects an
    /// authenticated-but-unauthorized request, so this is a 302 to <c>AccessDeniedPath</c>, not a
    /// 403 (Cantina's rule).
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
