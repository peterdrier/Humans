using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Gate page through the real app.</summary>
/// <remarks>
/// The kiosk <c>_ViewStart</c> names the section's own <c>_GateLayout</c> by string, which resolves (or throws) only at
/// request time, and an RCL asset can lose its <c>?v=</c> hash silently. Gate has no resource set: the kiosk is
/// English-only by design.
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

        // Body class and title suffix come from _GateLayout.
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
        // An RCL asset comes from the static-web-assets manifest; a file provider that can't see it emits the bare href
        // with no ?v= rather than failing.
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
        // An href resolving doesn't mean the file is served.
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
        }
    }

    [HumansFact(Timeout = 60000)]
    public async Task Admin_gate_pages_keep_the_shared_admin_layout()
    {
        // The mirror case: section views resolving Shell's _AdminLayout.
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
