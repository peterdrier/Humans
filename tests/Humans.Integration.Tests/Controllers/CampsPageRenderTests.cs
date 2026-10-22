using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Camps page through the real app.</summary>
/// <remarks>
/// <para>
/// Failures render as a 200 with degraded content: a missing <c>@@addTagHelper *, Humans.Camps</c> drops
/// <c>&lt;vc:my-camps&gt;</c>, a mis-bound key renders as its name, and only Spanish proves the satellites ship.
/// </para>
/// <para>
/// <c>Common_*</c>, <c>CityPlanning_*</c>, the Base facility enums and <c>Camp_Plural</c> stay shared, read through
/// <c>SharedLocalizer</c>: <c>Camp_Plural</c> because Shell and other sections render it too. The <c>NotContain</c>
/// checks catch a half-switched call site.
/// </para>
/// </remarks>
public class CampsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// Pages that render without a seeded camp. The detail, edit and season pages need a camp row, and seeding one is
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

            html.Should().NotContain("Camp_", $"GET {url} rendered a raw Camps resource key");
            html.Should().NotContain("Camps_", $"GET {url} rendered a raw Camps resource key");
            html.Should().NotContain("CampCard_", $"GET {url} rendered a raw CampCard key");

            // The mirror: a shared key read off the section set.
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
            because: "Camps_Index_BarrioGuide resolves through IStringLocalizer<CampsResource>");
        html.Should().Contain("Filter",
            because: "Camps_Index_FilterButton is on the section's own filter bar");
        // An unbound <vc:> ships as inert markup, so assert the emitted modal id, not only the absence of "<vc:".
        html.Should().Contain("sectionHelp-Camps",
            because: "<vc:access-matrix section=\"Camps\" /> must bind and emit its modal");
    }

    /// <summary>
    /// <c>Camp_Plural</c> stayed in <c>SharedResource</c> because Shell's nav renders it; a mis-bind is a raw key on
    /// every camp page.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_shared_camp_plural_key_still_resolves_on_both_sides()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var sectionPage = await (await Client.GetAsync("/Camps/Register", ct))
            .Content.ReadAsStringAsync(ct);
        sectionPage.Should().NotContain("Camp_Plural");

        var shellPage = await (await Client.GetAsync("/", ct)).Content.ReadAsStringAsync(ct);
        shellPage.Should().NotContain("Camp_Plural");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Camps_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // HtmlEncoder escapes non-ASCII ("Guía" reaches the body as "Gu&#xED;a"), so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Camps", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Camps", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("del Barrio");   // Camps_Index_BarrioGuide
        html.Should().Contain("Filtrar");      // Camps_Index_FilterButton
        html.Should().NotContain("Barrio Guide 2026");
        html.Should().NotContain("Camp_");
        html.Should().NotContain("Camps_");
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
