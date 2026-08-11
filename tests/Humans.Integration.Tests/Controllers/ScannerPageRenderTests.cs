using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Scanner page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Scanner</c>
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
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops. Scanner's
/// ticket card is the case that forced <c>TicketStubViewComponent</c> down from
/// <c>Humans.Web</c> into <c>Humans.UI</c>, which a section _can_ name.
/// </description></item>
/// <item><description>
/// The resx carve moved 38 <c>Scanner_*</c> keys out of <c>SharedResource</c>. A key the carve
/// missed, or a call site bound to the wrong set, renders the raw key — in every language, with
/// no error. The Spanish request is the only thing that proves the section RCL's satellite
/// assemblies reach the host's probing path at all.
/// </description></item>
/// <item><description>
/// The two scanner JS modules moved to the section's RCL and are served from the
/// static-web-assets manifest rather than from <c>wwwroot</c> on disk, so
/// <c>IFileVersionProvider</c> can silently emit no <c>?v=</c> hash while the page still
/// returns 200 (§15 step 7).
/// </description></item>
/// </list>
/// </remarks>
public class ScannerPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string BarcodeJsPath = "/_content/Humans.Scanner/js/scanner/barcode.js";
    private const string TicketsJsPath = "/_content/Humans.Scanner/js/scanner/tickets.js";

    private static (string Url, string Copy)[] Pages =>
    [
        ("/Scanner", "In-browser scanner tools"),           // Scanner_Index_Intro
        ("/Scanner/Barcode", "Barcode scanner"),            // Scanner_Barcode_Title
        ("/Scanner/Tickets", "Scan a ticket to see"),       // Scanner_Tickets_Intro
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_scanner_page_renders_its_own_copy_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own resolved copy");

            // A view component element that the section's _ViewImports failed to bind renders
            // as literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // The fallback for a key the carve missed is the key itself.
            html.Should().NotContain("Scanner_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_ticket_card_renders_the_stub_component_the_section_can_actually_bind()
    {
        // /Scanner/Tickets/Card is the section's only partial, and the one place a section view
        // uses <vc:ticket-stub>. The component moved from Humans.Web to Humans.UI for exactly
        // this reason — a Shell-resident component is inert markup from a section view. The
        // not-found branch proves the partial renders at all; the found branch needs a seeded
        // ticket, which the section's unit tests cover against substituted reads.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Scanner/Tickets/Card?barcode=no-such-barcode", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("no-such-barcode");
        html.Should().NotContain("Scanner_", "the not-found copy is a carved key");
        html.Should().NotContain("<vc:");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Scanner_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // An English-only check passes whether or not the RCL's satellites shipped — the neutral
        // set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities ("Escaner" carries an
        // accent and reaches the body as "Esc&#xE1;ner"), so the assertions stay on ASCII-only runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Scanner page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Scanner", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Scanner/Barcode", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("digos de barras");            // Scanner_Barcode_Title
        html.Should().Contain("no se usa para el check-in");  // Scanner_Barcode_NotForCheckIn
        html.Should().NotContain("Barcode scanner");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Scanner_pages_link_the_sections_static_assets_with_cache_busting_hashes()
    {
        // §15 step 7. Both modules are imported through IFileVersionProvider rather than a tag
        // helper, because asp-append-version cannot reach an ES module import statement.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, asset) in new[]
                 {
                     ("/Scanner/Barcode", BarcodeJsPath),
                     ("/Scanner/Tickets", TicketsJsPath),
                 })
        {
            var html = await (await Client.GetAsync(url, ct)).Content.ReadAsStringAsync(ct);
            html.Should().Contain($"{asset}?v=", $"GET {url} must cache-bust {asset}");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_sections_static_assets_are_served_from_its_own_RCL()
    {
        // The other half of §15 step 7: the URL resolving is not the same as the file being
        // there. Before the test host composed the static-web-assets manifest an RCL asset
        // 404'd here while the page still returned 200. tickets.js also relative-imports
        // barcode.js, so both must sit under the same served prefix.
        var ct = Xunit.TestContext.Current.CancellationToken;

        foreach (var asset in new[] { BarcodeJsPath, TicketsJsPath })
        {
            var response = await Client.GetAsync(asset, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {asset} must be served");
        }
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
