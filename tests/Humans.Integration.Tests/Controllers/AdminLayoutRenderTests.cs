using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The page chrome — <c>_AdminLayout</c>, <c>_LoginPartial</c>, <c>_LanguageChooser</c>,
/// <c>_AuthorizationPill</c>, <c>_VersionInfo</c> — moved out of <c>Humans.UI</c> into
/// <c>Humans.Web</c> in G5 lane 4b-ii (nobodies-collective/Humans#866). This file is the
/// standing proof that the move did not quietly take the chrome off any page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a render test and not the build.</b> A layout or partial that fails to resolve by
/// name across application parts is not a compile error and, for a partial, not even a
/// runtime one: 24 projects reach <c>_AdminLayout</c> through nothing but the string
/// <c>Layout = "_AdminLayout"</c>, and <c>&lt;partial name="…"/&gt;</c> resolution is the same
/// late, name-based lookup. The failure ships as a 200 with the nav, sidebar and breadcrumb
/// simply absent. So the assertions are on markers unique to the chrome — the
/// <c>admin-shell</c> body class, the "Exit admin" link, <c>_LoginPartial</c>'s
/// <c>data-testid="user-nav"</c> — never on the status code alone.
/// </para>
/// <para>
/// <b>Cross-part resolution in this direction was already proven</b> by
/// <c>Humans.Tour/Views/Tour/_ViewStart.cshtml</c>, which resolves <c>_Layout</c> out of
/// <c>Humans.Web</c>. What was new in 4b-ii is the volume: 41 call sites (27
/// <c>_ViewStart.cshtml</c> files and 14 inline <c>Layout = "_AdminLayout"</c> overrides)
/// across 24 projects. The render tests below cover one page from most of them; the two
/// source scans cover the whole set structurally, and cover whatever call site is added next.
/// </para>
/// </remarks>
public partial class AdminLayoutRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [GeneratedRegex("Layout\\s*=.*\"_AdminLayout\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AdminLayoutAssignment();

    [GeneratedRegex("<partial\\s+name=\"(?<name>[^\"]+)\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PartialTag();

    /// <summary>Body class set only by <c>_AdminLayout</c>; the member <c>_Layout</c> has no such class.</summary>
    private const string AdminShellMarker = "admin-shell";

    /// <summary>The admin nav's exit link — <c>_AdminLayout</c>'s own copy, in no other view.</summary>
    private const string AdminNavMarker = "Exit admin";

    /// <summary><c>_LoginPartial</c>'s signed-in dropdown, rendered from inside both layouts.</summary>
    private const string LoginPartialMarker = "data-testid=\"user-nav\"";

    /// <summary>
    /// One page per assembly that names <c>_AdminLayout</c>, drawn from the URLs each
    /// section's own render test already proves reachable for the Admin persona. The
    /// second element records which form the site takes, so a failure names the shape.
    /// </summary>
    private static (string Url, string Site)[] AdminPages =>
    [
        ("/AuditLog", "Humans.AuditLog — Views/AuditLog/_ViewStart"),
        ("/Finance", "Humans.Budget — Views/BudgetAdmin/_ViewStart"),
        ("/Campaigns/Admin", "Humans.Campaigns — Views/Campaign/_ViewStart"),
        ("/Cantina/Roster", "Humans.Cantina — Views/Cantina/_ViewStart"),
        ("/Legal/Admin/Documents", "Humans.Consent — Views/AdminLegalDocuments/_ViewStart"),
        ("/Debug/Logs", "Humans.Debug — Views/Debug/_ViewStart"),
        ("/Shifts/Admin/EarlyEntry", "Humans.EarlyEntry — Views/EarlyEntryRoster/_ViewStart"),
        ("/Email/EmailOutbox", "Humans.Email — Views/Email/_ViewStart"),
        ("/Finance/Creditors", "Humans.Finance — Views/Finance/_ViewStart"),
        ("/Gate/Admin", "Humans.Gate — inline override in Gate/Admin.cshtml"),
        ("/Google", "Humans.GoogleIntegration — Views/Google/_ViewStart"),
        ("/Governance/BoardVoting", "Humans.Governance — Views/Governance/BoardVoting/_ViewStart"),
        ("/Governance/Applications/Admin", "Humans.Governance — inline override in Applications/Admin.cshtml"),
        ("/Mailer/Admin", "Humans.Mailer — Views/Mailer/Admin/_ViewStart"),
        ("/OnboardingReview", "Humans.Onboarding — Views/OnboardingReview/_ViewStart"),
        ("/Scanner", "Humans.Scanner — Views/Scanner/_ViewStart"),
        ("/Shifts/Dashboard", "Humans.Shifts — Views/ShiftDashboard/_ViewStart"),
        ("/Shifts/Summary", "Humans.Shifts — inline override in Shifts/Summary.cshtml"),
        ("/Store/Admin/Catalog", "Humans.Store — Views/StoreAdmin/_ViewStart"),
        ("/Tickets", "Humans.Tickets — Views/Ticket/_ViewStart"),
        ("/Tickets/Admin/Transfers", "Humans.Tickets — Views/TicketTransferAdmin/_ViewStart"),
    ];

    /// <summary>
    /// The chrome reaches a page in every assembly that names the layout, proved by markers
    /// the layout alone emits.
    /// </summary>
    [HumansFact(Timeout = 600000)]
    public async Task The_admin_layout_wraps_a_page_in_every_assembly_that_names_it()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // Humans.Users' busiest admin page, an inline override in UsersAdmin/AdminDetail.cshtml.
        var pages = AdminPages
            .Append(($"/Users/Admin/{adminId}", "Humans.Users — inline override in UsersAdmin/AdminDetail.cshtml"))
            .ToList();

        foreach (var (url, site) in pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render ({site})");

            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().Contain(AdminShellMarker,
                $"GET {url} did not render inside _AdminLayout — a layout that fails to resolve "
                + $"across application parts takes the whole admin shell off the page ({site})");
            html.Should().Contain(AdminNavMarker,
                $"GET {url} reached the admin body class but not the admin nav ({site})");
            html.Should().Contain(LoginPartialMarker,
                $"GET {url}: _AdminLayout's <partial name=\"_LoginPartial\" /> did not render — "
                + $"an unresolved partial is silent, not an error ({site})");

            // <vc:temp-data-alerts /> is bound by Humans.Web's own _ViewImports; unbound, it
            // survives into the body as literal markup the browser drops.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered ({site})");
            html.Should().NotContain("<partial ", $"GET {url} left a <partial> tag helper unbound ({site})");
        }
    }

    /// <summary>
    /// <c>_AuthorizationPill</c> moved with the two layouts that are its only renderers.
    /// </summary>
    /// <remarks>
    /// The pill renders nothing unless <c>Humans.Web</c>'s <c>AuthorizationPillFilter</c> put
    /// <c>ViewData["AuthPillRoles"]</c> there, so "the page returned 200" and even "no literal
    /// &lt;partial&gt; survived" both pass on a page that never rendered it. <c>/Debug/Logs</c>
    /// is <c>AdminOnly</c>, which is the filter's "Admin only" case, so the pill's own text is
    /// the marker.
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_authorization_pill_renders_inside_the_admin_layout()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Debug/Logs", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("auth-pill",
            "_AdminLayout's <partial name=\"_AuthorizationPill\" /> must resolve — it moved out of "
            + "Humans.UI alongside its only two renderers");
        html.Should().Contain("Admin only",
            "the pill must carry AuthorizationPillFilter's text, not just an empty wrapper");
    }

    /// <summary>
    /// The member shell's own two chrome partials, <c>_LanguageChooser</c> and
    /// <c>_LoginPartial</c>, still render from <c>_Layout</c> after the move.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_member_layout_still_renders_its_chrome_partials()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Shifts", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().NotContain(AdminShellMarker, "/Shifts renders in the member shell, not the admin one");
        html.Should().Contain("/Language/SetLanguage",
            "_LanguageChooser posts each language to LanguageController — an unresolved partial "
            + "leaves the topbar with no language switch and no error");
        html.Should().Contain(LoginPartialMarker,
            "_Layout's <partial name=\"_LoginPartial\" /> must resolve from Humans.Web");
    }

    /// <summary>
    /// Every <c>Layout = "_AdminLayout"</c> in the tree resolves to exactly one layout file,
    /// and that file sits in Shell's <c>Views/Shared</c>.
    /// </summary>
    /// <remarks>
    /// The render test above covers one page per assembly. This covers the other call sites —
    /// and every call site added later — structurally, because a Roslyn analyzer cannot see
    /// <c>.cshtml</c> (peters-hard-rules.md prefers analyzers wherever they can reach). Two
    /// copies of the layout would be worse than none: whichever the view engine found first
    /// would win silently, per request.
    /// </remarks>
    [HumansFact]
    public void Every_admin_layout_call_site_resolves_to_the_one_layout_in_the_shell()
    {
        var src = Path.Combine(FindRepoRoot(), "src");

        var callSites = RazorViews(src)
            .Where(f => AdminLayoutAssignment().IsMatch(File.ReadAllText(f)))
            .ToList();

        callSites.Should().NotBeEmpty("the admin layout would otherwise be dead");

        var layouts = RazorViews(src)
            .Where(f => Path.GetFileName(f).Equals("_AdminLayout.cshtml", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        layouts.Should().ContainSingle(
            $"{callSites.Count} call sites resolve _AdminLayout by name alone; a second copy in "
            + "another application part would win for some of them and not others, silently");
        layouts[0].Should().Be("Humans.Web/Views/Shared/_AdminLayout.cshtml",
            "the chrome is the Shell's since G5 lane 4b-ii — a section may not reference Shell, so "
            + "no section can own the layout every section's admin pages sit in");
    }

    /// <summary>
    /// Every <c>&lt;partial name="…"/&gt;</c> the two layouts render resolves to exactly one file.
    /// </summary>
    /// <remarks>
    /// The chrome partials travel with the layouts. Moving a layout and leaving one of its
    /// partials behind still compiles and still returns 200 — the partial just renders nothing
    /// — so this pins the set rather than the individual names 4b-ii happened to move.
    /// </remarks>
    [HumansFact]
    public void Every_partial_the_layouts_render_resolves_to_exactly_one_file()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var views = RazorViews(src).ToList();

        var layouts = views
            .Where(f => Path.GetFileName(f) is "_Layout.cshtml" or "_AdminLayout.cshtml")
            .ToList();
        layouts.Should().NotBeEmpty();

        var partialNames = layouts
            .SelectMany(f => PartialTag().Matches(File.ReadAllText(f))
                .Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        partialNames.Should().NotBeEmpty("both layouts render chrome partials by name");

        foreach (var name in partialNames)
        {
            views
                .Where(f => Path.GetFileName(f).Equals($"{name}.cshtml", StringComparison.Ordinal))
                .Should().ContainSingle(
                    $"the layouts render <partial name=\"{name}\" />, which resolves by name across "
                    + "application parts — zero copies renders nothing and two is a coin flip, and "
                    + "neither is an error");
        }
    }

    /// <summary>
    /// Every Shifts page short-circuits to a redirect when no burn is active, and a 302 proves
    /// nothing about chrome. Seeded through the section's own leaf verb, not its DbContext —
    /// the section's view is a Singleton decorator warmed before the test body runs
    /// (ShiftsPageRenderTests' finding).
    /// </summary>
    private async Task SeedActiveBurnAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var seeding = scope.ServiceProvider.GetRequiredService<IShiftSeeding>();

        await seeding.DeactivateActiveBurnAsync();
        await seeding.CreateBurnAsync(new CreateBurnInput(
            Id: Guid.NewGuid(),
            EventName: "Admin Chrome Test Burn",
            Year: 2026,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 1),
            BuildStartOffset: -14,
            EventEndOffset: 6,
            StrikeEndOffset: 9,
            IsShiftBrowsingOpen: true));
    }

    private static IEnumerable<string> RazorViews(string srcRoot) =>
        Directory
            .EnumerateFiles(srcRoot, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
