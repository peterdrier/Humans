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
/// <item><description>
/// A key the resx carve missed, or a call site left on the wrong one of the section's two
/// localizers, renders as its own raw key name — in all six languages, on a green 200. The
/// English pass alone would still pass if the RCL's satellite assemblies never shipped, so
/// the Spanish round trip is a separate test.
/// </description></item>
/// </list>
/// </remarks>
public class ShiftsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string ShiftsScriptPath = "/_content/Humans.Shifts/js/shifts.js";

    /// <summary>
    /// The six key prefixes the carve moved into <c>ShiftsResource</c>. None of them may
    /// appear in a rendered body: a missed key, or a call site still on <c>SharedLocalizer</c>
    /// after its key moved, falls back to the key name rather than throwing. The section's
    /// markup carries these strings nowhere but inside a <c>Localizer[…]</c> index and two
    /// Razor comments, so a literal-substring assertion is exact.
    /// </summary>
    private static readonly string[] CarvedKeyPrefixes =
        ["Shifts_", "ShiftDash_", "ShiftInfo_", "VolTrack_", "EmailRota_", "EmailTeamRotas_"];

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

            // Same failure class, different cause: an uncarved or mis-bound key falls back to
            // its own name instead of throwing.
            foreach (var prefix in CarvedKeyPrefixes)
                html.Should().NotContain(prefix, $"GET {url} rendered a raw {prefix}* key");
        }
    }

    /// <summary>
    /// AccessMatrixViewComponent moved into <c>Humans.UI</c> (nobodies-collective/Humans#1056),
    /// so <c>/Shifts</c> carries <c>&lt;vc:access-matrix section="Shifts" /&gt;</c> bound through
    /// <c>@@addTagHelper *, Humans.UI</c>. The blanket <c>NotContain("&lt;vc:")</c> above cannot
    /// see a Razor comment being stripped instead of a tag binding, and the page also hard-codes
    /// <c>data-bs-target="#sectionHelp-Shifts"</c> on its "Learn more" button — so the proof has
    /// to be the <c>id=</c> attribute only the component emits.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_browse_page_renders_the_access_matrix_widget()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Shifts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().NotContain("<vc:", "GET /Shifts left a view-component tag unrendered");
        html.Should().Contain("id=\"sectionHelp-Shifts\"",
            "the access-matrix modal is what the page's Learn-more button targets");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shifts_pages_render_spanish_from_the_sections_own_satellite_assemblies()
    {
        // The English pass above proves nothing about the carve's five translations: the
        // neutral set is embedded in the section assembly and the culture fallback is silent,
        // so a satellite that never shipped still renders English on a green 200.
        //
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so every expected
        // run below is ASCII-only on purpose.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Accept-Language does not reach a signed-in user — Program.cs's initial culture
        // provider returns the user's PreferredLanguage and short-circuits the rest of the
        // chain — and every Shifts page is [Authorize]. Switch the way the UI does
        // (CityPlanning's rule).
        var switcherPage = await (await Client.GetAsync("/Shifts", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        // One page per carve group whose renderer differs: a section view, the two actions
        // carved off Shell's ProfileController, and the controller that moved in whole.
        foreach (var (url, spanish) in new[]
                 {
                     ("/Shifts/Dashboard", "Panel de turnos"),                    // ShiftDash_Title
                     ("/Profile/Me/ShiftInfo", "Preferencias de turnos"),         // ShiftInfo_Title
                     ("/Shifts/Dashboard/VolunteerTracking", "Seguimiento de Voluntarios"), // VolTrack_Title
                 })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(spanish, $"GET {url} must resolve its own set's es satellite");

            foreach (var prefix in CarvedKeyPrefixes)
                html.Should().NotContain(prefix, $"GET {url} rendered a raw {prefix}* key in Spanish");
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
        // the table only through SectionControllerFeatureProvider.
        //
        // Asserted here rather than off a rendered page, and the fixture is not what stands
        // in the way: /WidgetGallery renders <vc:things-to-do … has-shift-signups="true" />
        // with the flag hard-coded, so the branch needs no signup at all — but the component
        // returns Content(string.Empty) for a fully-onboarded admin on both /WidgetGallery
        // and /Home/Dashboard, so there is no card to read an href off. That is Shell
        // behaviour on Shell's own inputs (this section changed none of them, and a carved
        // resource key returns its own name rather than throwing). Residual gap, stated:
        // this proves the pair the component writes today resolves, not that the component
        // still writes it.
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

    [HumansFact(Timeout = 60000)]
    public void No_call_site_still_addresses_shift_info_on_the_profile_controller()
    {
        // The test above proves the pair resolves; it cannot see a caller that writes the
        // *old* pair. Codex found exactly that on this PR — Profile/Index.cshtml still had
        // asp-controller="Profile" asp-action="ShiftInfo", which the anchor tag helper
        // resolves to no href at all, on a green 200. Same failure as the Url.Action form,
        // reached through Razor instead of C#, so the grep covers both spellings.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Humans.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("the test must be able to find the repo root to scan sources");

        var stale = Directory
            .EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(line =>
                (line.Contains("asp-action=\"ShiftInfo\"", StringComparison.Ordinal)
                 && line.Contains("asp-controller=\"Profile\"", StringComparison.Ordinal))
                || line.Contains("Url.Action(\"ShiftInfo\", \"Profile\")", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root.FullName, f))
            .ToList();

        stale.Should().BeEmpty(
            "ShiftInfo lives on ShiftProfileController now; addressing it as (\"ShiftInfo\", \"Profile\") renders a link with no href instead of failing");
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
