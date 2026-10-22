using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Scanner page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// only Spanish proves the satellites ship, and the RCL's JS modules can lose their <c>?v=</c> hash silently.
/// </remarks>
public class ScannerPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string BarcodeJsPath = "/_content/Humans.Scanner/js/scanner/barcode.js";
    private const string TicketsJsPath = "/_content/Humans.Scanner/js/scanner/tickets.js";

    private static (string Url, string Copy)[] Pages =>
    [
        ("/Scanner", "Camera tools for ticket staff"),      // Scanner_Index_Intro
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

            html.Should().NotContain("Scanner_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_ticket_card_renders_the_stub_component_the_section_can_actually_bind()
    {
        // The only section view using <vc:ticket-stub>; a Shell-resident component would be inert here. The not-found
        // branch proves the partial renders; the found branch needs a seeded ticket.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Scanner/Tickets/Card?barcode=no-such-barcode", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("no-such-barcode");
        html.Should().NotContain("Scanner_", "the not-found copy is a carved key");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Scanner_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // Only Spanish proves the satellites shipped. HtmlEncoder escapes non-ASCII ("Escáner" becomes "Esc&#xE1;ner"),
        // so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Scanner", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
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
        // The modules go through IFileVersionProvider because asp-append-version can't reach an ES module import.
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
        // A URL resolving doesn't mean the file is served. tickets.js relative-imports barcode.js, so both must share
        // the served prefix.
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
