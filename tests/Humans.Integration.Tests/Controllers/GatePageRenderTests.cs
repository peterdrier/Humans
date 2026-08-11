using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Gate page through the real app, as the standing form of the §15 step 12 check
/// for the section's move into <c>src/Sections/Humans.Gate</c>
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
/// </description></item>
/// <item><description>
/// Gate is the first section to take a <b>layout</b> with it: <c>_GateLayout</c> moved out of
/// <c>Humans.UI/Views/Shared</c> into the section's own <c>Views/Shared</c>, and the kiosk
/// <c>_ViewStart</c> names it by string. Layout resolution across application parts is what
/// these tests prove — a miss throws, but only at request time.
/// </description></item>
/// <item><description>
/// A static asset that moved to the section's RCL is served from the static-web-assets manifest
/// rather than from <c>wwwroot</c> on disk, so <c>asp-append-version</c> can silently emit no
/// <c>?v=</c> hash while the page still returns 200 (§15 step 7).
/// </description></item>
/// </list>
/// <para>
/// There is no resource-key case and no Spanish case: Gate carries no <c>.resx</c> set at all.
/// Every string on the kiosk is inline English by design (staff-facing, single-locale terminal),
/// so the carve took nothing and there is no fallback-to-raw-key mode to catch. The structural
/// half of that is <c>GateArchitectureTests.SectionTypesTakeNoStringLocalizer</c>.
/// </para>
/// </remarks>
public class GatePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string GateCssPath = "/_content/Humans.Gate/css/gate/gate.css";
    private const string GateJsPath = "/_content/Humans.Gate/js/gate/gate.js";
    private const string GateKeypadJsPath = "/_content/Humans.Gate/js/gate/gate-keypad.js";
    private const string GatePinJsPath = "/_content/Humans.Gate/js/gate/gate-pin.js";

    [HumansFact(Timeout = 60000)]
    public async Task Scan_terminal_renders_through_the_sections_own_kiosk_layout()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Gate", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Body class and title suffix come from _GateLayout, which now lives in the section.
        html.Should().Contain("gate-kiosk",
            because: "the kiosk _ViewStart names _GateLayout by string; it must still resolve "
                   + "now that the layout lives in the section's own Views/Shared");
        html.Should().Contain("· Gate</title>");
        html.Should().Contain("Scan a ticket");
        html.Should().Contain("Supervisor override");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Kiosk_pages_link_the_sections_static_assets_with_cache_busting_hashes()
    {
        // §15 step 7: asp-append-version resolves the file through the host's IFileProvider.
        // Once the file lives in an RCL it comes from the static-web-assets manifest instead of
        // wwwroot on disk, and a provider that cannot see it emits the bare href with no ?v=
        // rather than failing.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Gate", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain($"href=\"{GateCssPath}?v=");
        html.Should().Contain($"src=\"{GateKeypadJsPath}?v=");
        html.Should().Contain(GateJsPath + "?v=",
            because: "gate.js is imported through IFileVersionProvider rather than a tag helper");
    }

    [HumansFact(Timeout = 60000)]
    public async Task The_sections_static_assets_are_served_from_its_own_RCL()
    {
        // The other half of §15 step 7: the href resolving is not the same as the file being
        // there. Before the test host composed the static-web-assets manifest an RCL asset
        // 404'd here while the page still returned 200.
        var ct = Xunit.TestContext.Current.CancellationToken;

        foreach (var asset in new[] { GateCssPath, GateJsPath, GateKeypadJsPath, GatePinJsPath })
        {
            var response = await Client.GetAsync(asset, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {asset} must be served");
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task Every_gate_page_renders_with_its_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var pages = new (string Url, string Copy)[]
        {
            ("/Gate", "Scan a ticket"),
            ("/Gate/Claim", "Start scanning"),
            ("/Gate/Leaderboard", "Gate leaderboard"),
            ("/Gate/Admin", "Gate settings"),
            ("/Gate/Admin/VendorCheckInBackfill", "Vendor check-in backfill"),
        };

        foreach (var (url, copy) in pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // A view component element that the section's _ViewImports failed to bind renders
            // as literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task Admin_gate_pages_keep_the_shared_admin_layout()
    {
        // /Gate/Admin and the backfill page override _ViewStart back to Shell's _AdminLayout —
        // a section view resolving a layout that lives in the *host*, the mirror of the
        // kiosk case above.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in new[] { "/Gate/Admin", "/Gate/Admin/VendorCheckInBackfill" })
        {
            var html = await (await Client.GetAsync(url, ct)).Content.ReadAsStringAsync(ct);
            html.Should().NotContain("gate-kiosk", $"GET {url} is a desktop admin page");
            html.Should().Contain("admin", $"GET {url} must render through the admin chrome");
        }
    }
}
