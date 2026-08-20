using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Camps page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Camps</c>
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
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build. Camps'
/// <c>&lt;vc:my-camps&gt;</c> is public under the section's <c>Contracts/</c> folder, so the
/// tag helper is generated and Shell resolves it off one <c>@@addTagHelper *, Humans.Camps</c>
/// line; the check here is what proves that line is present rather than the element being
/// silently dropped by the browser.
/// </description></item>
/// <item><description>
/// The resx carve moved 112 <c>Camp_*</c>, <c>CampCard_*</c> and <c>Enum_CampVibe_*</c> keys
/// out of <c>SharedResource</c>. A key the carve missed, or a call site bound to the wrong set,
/// renders the raw key — in every language, with no error.
/// </description></item>
/// <item><description>
/// Four prefixes deliberately stayed shared and are read through <c>SharedLocalizer</c>:
/// <c>Common_*</c>, <c>CityPlanning_*</c>, the Base facility enums (<c>Enum_YesNoMaybe_*</c>,
/// <c>Enum_SoundZone_*</c>, <c>Enum_SpaceSize_*</c> and friends, which stayed with their enums
/// in <c>Humans.Domain</c>) and <c>Camp_Plural</c> — the last because Shell's nav and
/// dashboard, the guest landing page, Containers' breadcrumb and Events' barrio form all
/// render it, and a key rendered outside the section cannot see the section's set. A
/// half-switched call site renders the raw key with a green build, which is what the
/// <c>NotContain</c> assertions on those prefixes catch.
/// </description></item>
/// <item><description>
/// The Spanish request is the only thing that proves the section RCL's satellite assemblies
/// reach the host's probing path at all — an English-only check passes either way, because the
/// neutral set is embedded in the main assembly and the fallback is silent.
/// </description></item>
/// </list>
/// <para>
/// Camps ships no <c>wwwroot/</c>, so there is no static-asset half here. The role and
/// compliance admin pages are covered too: they are the section's other two controllers, and
/// their copy sits on the failure paths a page-loads suite does not reach.
/// </para>
/// </remarks>
public class CampsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// Every page the section routes that renders without a seeded camp. The detail, edit and
    /// season pages need a camp row and are exercised through <c>/Camps</c>'s card grid
    /// instead — seeding a camp here would fixture the whole registration flow, which is
    /// <c>Humans.Camps.Tests</c>' business.
    /// </summary>
    private static readonly string[] Pages =
    [
        "/Camps",
        "/Camps/Register",
        "/Camps/Admin",
        "/Camps/Admin/Roles",
        "/Camps/Admin/Roles/Create",
        "/Camps/Admin/Compliance",
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_camps_page_renders_without_raw_resource_keys_or_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            // The fallback for a key the carve missed is the key itself.
            html.Should().NotContain("Camp_", $"GET {url} rendered a raw Camps resource key");
            html.Should().NotContain("CampCard_", $"GET {url} rendered a raw CampCard key");

            // …and the mirror: a shared key the section still reads, left on the wrong localizer.
            html.Should().NotContain("Common_", $"GET {url} read a shared key off the section set");
            html.Should().NotContain("CityPlanning_", $"GET {url} read CityPlanning copy off the section set");
            html.Should().NotContain("Enum_", $"GET {url} rendered a raw enum key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_camps_index_renders_the_sections_own_copy()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Camps", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("Barrio Guide",
            because: "Camp_Index_BarrioGuide resolves through IStringLocalizer<CampsResource>");
        html.Should().Contain("Filter",
            because: "Camp_Index_FilterButton is on the section's own filter bar");
        // <vc:access-matrix> binds through @addTagHelper *, Humans.Interfaces since the component
        // moved out of Shell (nobodies-collective/Humans#1056). An unbound <vc:> ships as
        // inert literal markup with a green build, so assert the emitted modal id, not just
        // the absence of "<vc:" above.
        html.Should().Contain("sectionHelp-Camps",
            because: "<vc:access-matrix section=\"Camps\" /> must bind and emit its modal");
    }

    /// <summary>
    /// <c>Camp_Plural</c> is the one key of the section's own prefix that stayed in
    /// <c>SharedResource</c>, because Shell's nav renders it. The section's breadcrumbs read it
    /// through <c>SharedLocalizer</c>; a mis-bind is a raw key on every camp page and neither
    /// the carve grep nor the build sees it.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_shared_camp_plural_key_still_resolves_on_both_sides()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var sectionPage = await (await Client.GetAsync("/Camps/Register", ct))
            .Content.ReadAsStringAsync(ct);
        sectionPage.Should().NotContain("Camp_Plural");

        // Shell's own renderer of the same key, which is why it stayed behind.
        var shellPage = await (await Client.GetAsync("/", ct)).Content.ReadAsStringAsync(ct);
        shellPage.Should().NotContain("Camp_Plural");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Camps_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy ("Guía del Barrio" reaches the body
        // as "Gu&#xED;a del Barrio").
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Camps admin page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Camps", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Camps", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("del Barrio");   // Camp_Index_BarrioGuide
        html.Should().Contain("Filtrar");      // Camp_Index_FilterButton
        html.Should().NotContain("Barrio Guide 2026");
        html.Should().NotContain("Camp_");
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return match.Success ? match.Groups["token"].Value : null;
    }
}
