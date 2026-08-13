using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Shifts page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Shifts</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The page list is written from the <b>pre-move</b> route table, deliberately: a table
/// written from the post-move state cannot catch a page the move deleted, which is how a
/// previous batch dropped <c>/Tickets/GateList</c> in silence.
/// </para>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops.
/// </description></item>
/// <item><description>
/// <c>Url.Action("ShiftInfo", "Profile")</c> in Shell's <c>ThingsToDoViewComponent</c>
/// returns <b>null</b> once the action moves onto <c>ShiftProfileController</c>, so the
/// to-do item renders without a link on a green 200 — indistinguishable from its
/// "not applicable" branch. Nothing else in the repo guards a bare <c>Url.Action</c>
/// string pair.
/// </description></item>
/// <item><description>
/// <c>wwwroot/js/shifts.js</c> became <c>/_content/Humans.Shifts/js/shifts.js</c>. A missed
/// rewrite 404s the script and <c>asp-append-version</c> silently emits no <c>?v=</c> hash
/// rather than throwing, so both halves are asserted.
/// </description></item>
/// </list>
/// <para>
/// The resx carve is the <em>next</em> commit, so every <c>Shifts_*</c> / <c>ShiftDash_*</c> /
/// <c>ShiftInfo_*</c> key is still in <c>SharedResource</c> and there is no raw-key assertion
/// to make yet; it belongs with the carve.
/// </para>
/// </remarks>
public class ShiftsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string ShiftsScriptPath = "/_content/Humans.Shifts/js/shifts.js";

    /// <summary>
    /// Every GET route the section owned before the move that renders without a fixture.
    /// The parameterised reads — <c>/Shifts/Summary/{teamSlug}</c>,
    /// <c>/Teams/{slug}/Shifts</c> and the two rota-email pages — need seeded rotas and are
    /// covered by the section's own controller tests.
    /// </summary>
    private static (string Url, string Copy)[] Pages =>
    [
        ("/Shifts", "Shifts"),
        ("/Shifts/Mine", "Shifts"),
        ("/Shifts/Summary", "Summary"),
        ("/Shifts/Settings", "Settings"),
        ("/Shifts/OrphanSignups", "Orphan"),
        ("/Shifts/Dashboard", "Dashboard"),
        ("/Shifts/Dashboard/PostEventStats", "Stats"),
        ("/Shifts/Admin/Workload", "Workload"),
        ("/Shifts/Dashboard/VolunteerTracking", "Volunteer"),
        // Two actions carved off Shell's ProfileController; [Route("Profile")] stayed on
        // both halves, so the URL is unchanged.
        ("/Profile/Me/ShiftInfo", "Shift"),
    ];

    /// <summary>
    /// Every page in <see cref="Pages"/> except <c>/Shifts/Mine</c> and
    /// <c>/Profile/Me/ShiftInfo</c> short-circuits to a redirect or a
    /// <c>NoActiveEvent</c> view when no burn is active, so the fixture would render four
    /// words of chrome and the assertions would prove nothing. Seed through the section's
    /// own leaf verb rather than through <c>ShiftsDbContext</c>: the section's view is a
    /// Singleton decorator warmed at startup, i.e. before the test body runs, so a direct
    /// context write is invisible to every cached read (Calendar's finding).
    /// </summary>
    private async Task SeedActiveBurnAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var seeding = scope.ServiceProvider.GetRequiredService<IShiftSeeding>();

        await seeding.DeactivateActiveBurnAsync();
        await seeding.CreateBurnAsync(new CreateBurnInput(
            Id: Guid.NewGuid(),
            EventName: "Render Test Burn",
            Year: 2026,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 1),
            BuildStartOffset: -14,
            EventEndOffset: 6,
            StrikeEndOffset: 9,
            IsShiftBrowsingOpen: true));
    }

    [HumansFact(Timeout = 180000)]
    public async Task Every_shifts_page_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_browse_page_links_the_sections_own_static_asset()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Shifts", ct)).Content.ReadAsStringAsync(ct);

        // The cache-buster half: WebApplicationFactory composes the static-web-assets
        // manifest, so a missing RCL asset drops the ?v= hash rather than throwing.
        html.Should().Contain($"src=\"{ShiftsScriptPath}?v=",
            "the browse page must link the section's script through /_content and get a version hash");

        // The file half.
        var asset = await Client.GetAsync(ShiftsScriptPath, ct);
        asset.StatusCode.Should().Be(HttpStatusCode.OK, "the RCL's static asset must be served");
    }

    [HumansFact(Timeout = 60000)]
    public void The_shift_info_action_pair_shell_addresses_still_resolves()
    {
        // Shell's ThingsToDoViewComponent addresses the shift-info page by (action,
        // controller) string pair: Url.Action("ShiftInfo", "ShiftProfile"). The move
        // rehomed that action off ProfileController, and an unresolvable pair returns
        // **null** rather than throwing — the to-do renders without an href, on a green
        // 200, indistinguishable from its "already filled in" branch. Nothing else in the
        // repo guards a bare Url.Action pair; AdminNavTreeRoutingTests is this same
        // mechanism applied to the nav tree, for the same reason.
        //
        // Read from IActionDescriptorCollectionProvider rather than by reflection, so it
        // sees what MVC really routes — the section's controllers are internal and reach
        // the table only through SectionControllerFeatureProvider. Asserted here rather
        // than off the rendered dashboard because the to-do item only appears for a
        // volunteer who already holds a signup, which costs a burn + department + rota +
        // shift + signup fixture spanning two sections to stand up.
        var actions = Factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .Where(a => a.RouteValues.TryGetValue("controller", out var c)
                        && string.Equals(c, "ShiftProfile", StringComparison.Ordinal)
                        && a.RouteValues.TryGetValue("action", out var m)
                        && string.Equals(m, "ShiftInfo", StringComparison.Ordinal))
            .ToList();

        actions.Should().NotBeEmpty(
            "ThingsToDoViewComponent addresses (\"ShiftInfo\", \"ShiftProfile\") and Url.Action returns null for an unresolvable pair");

        actions.Select(a => a.AttributeRouteInfo?.Template)
            .Should().AllBe("Profile/Me/ShiftInfo",
                "the [Route(\"Profile\")] prefix stayed on both halves of the split, so the URL is unchanged");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_shift_dashboard()
    {
        // The section's negative access rule. Worth pinning at the move because the
        // controllers are now internal types in another assembly, routed through
        // SectionControllerFeatureProvider, while ShiftDashboardAccess /
        // ShiftDepartmentManager stayed in Shell's AuthorizationPolicyExtensions (design
        // §8's asymmetry). The shape is a 302 to Program.cs's AccessDeniedPath, not a bare
        // 403 — cookie authentication redirects an authenticated-but-unauthorized request,
        // app-wide (Cantina's finding).
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[]
                 {
                     "/Shifts/Dashboard",
                     "/Shifts/Admin/Workload",
                     "/Shifts/Dashboard/VolunteerTracking",
                     "/Shifts/Settings",
                 })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"GET {url} must be denied, not sent to sign in");
        }
    }
}
