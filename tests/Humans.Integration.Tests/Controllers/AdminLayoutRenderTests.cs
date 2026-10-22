using Humans.Base.ViewComponents;
using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Integration.Tests.Infrastructure;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The admin and member chrome still reaches every page that names it.
/// </summary>
/// <remarks>
/// Layouts and partials resolve by name at runtime, so a miss is no compile error and, for a partial, not even a
/// runtime one: it ships as a 200 with the nav, sidebar and breadcrumb absent. So the assertions are on markers only
/// the chrome emits, never on the status code alone. The render tests cover one page per assembly; the source scans
/// cover every call site.
/// </remarks>
public partial class AdminLayoutRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [GeneratedRegex("Layout\\s*=.*\"_AdminLayout\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AdminLayoutAssignment();

    [GeneratedRegex("<partial\\s+name=\"(?<name>[^\"]+)\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PartialTag();

    /// <summary><c>class="active"</c> on the sidebar group row's anchor.</summary>
    [GeneratedRegex("<a class=\"active\" data-group=", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActiveSidebarLink();

    [GeneratedRegex("<a class=\"active\" aria-current=\"page\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActiveTab();

    /// <summary>Set only by <c>_AdminLayout</c>; the member <c>_Layout</c> has no such class.</summary>
    private const string AdminShellMarker = "admin-shell";

    /// <summary><c>_AdminLayout</c>'s own copy, in no other view.</summary>
    private const string AdminNavMarker = "Exit admin";

    /// <summary>Rendered from inside both layouts.</summary>
    private const string LoginPartialMarker = "data-testid=\"user-nav\"";

    /// <summary>
    /// One page per assembly that names <c>_AdminLayout</c>, from URLs each section's render test proves reachable for
    /// Admin. The second element names the call-site form, so a failure says which shape broke.
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
        ("/MailerLite/Admin", "Humans.MailerLite — Views/MailerLite/Admin/_ViewStart"),
        ("/OnboardingReview", "Humans.Onboarding — Views/OnboardingReview/_ViewStart"),
        ("/Scanner", "Humans.Scanner — Views/Scanner/_ViewStart"),
        ("/Shifts/Dashboard", "Humans.Shifts — Views/ShiftDashboard/_ViewStart"),
        ("/Shifts/Summary", "Humans.Shifts — inline override in Shifts/Summary.cshtml"),
        ("/Store/Admin/Catalog", "Humans.Store — Views/StoreAdmin/_ViewStart"),
        ("/Tickets", "Humans.Tickets — Views/Ticket/_ViewStart"),
        ("/Tickets/Admin/Transfers", "Humans.Tickets — Views/TicketTransferAdmin/_ViewStart"),
    ];

    [HumansFact(Timeout = 600000)]
    public async Task The_admin_layout_wraps_a_page_in_every_assembly_that_names_it()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedActiveBurnAsync();
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

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

            html.Should().NotContain("<partial ", $"GET {url} left a <partial> tag helper unbound ({site})");
        }
    }

    /// <summary>
    /// A full Admin sees every sidebar group, and every <c>AdminOnly</c> tab on Debug's group.
    /// </summary>
    /// <remarks>
    /// The E2E suite runs on QA, where <c>/dev/login/admin</c> is a 404, so the Admin row can only live here. Derived
    /// from <see cref="AdminNavComposition"/>, never a pinned list. Only <c>AdminOnly</c> items are asserted: any other
    /// policy would mean re-deriving the view component's own filter.
    /// </remarks>
    [HumansFact(Timeout = 180000)]
    public async Task The_sidebar_shows_an_admin_every_group_and_every_admin_only_item()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Admin", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        var groups = AdminNavComposition.Compose(
            Factory.Services.GetRequiredService<IEnumerable<ISectionAdminNav>>());

        foreach (var group in groups)
        {
            html.Should().Contain($"data-group=\"{group.Label}\"",
                $"an Admin must see the '{group.Label}' sidebar group");
        }

        var adminOnlyItems = groups
            .Single(g => string.Equals(g.Label, "Debug", StringComparison.Ordinal)).Items
            .Where(i => string.Equals(i.Policy, PolicyNames.AdminOnly, StringComparison.Ordinal))
            .ToList();

        adminOnlyItems.Should().NotBeEmpty("the tree's AdminOnly items are what this test covers");

        var tabsHtml = await Client.GetStringAsync("/Debug/Logs", ct);
        foreach (var item in adminOnlyItems)
        {
            tabsHtml.Should().Contain($"<span>{item.Label}</span>",
                $"an Admin must see the AdminOnly item '{item.Label}' — the tab strip renders each "
                + "item label in its own span");
        }
    }

    /// <summary>The dashboard's two <c>AdminOnly</c> panels render for a full Admin.</summary>
    /// <remarks>
    /// No E2E persona can assert them, and a tile that silently stops rendering looks the same as one the viewer was
    /// never entitled to.
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_admin_dashboard_renders_its_admin_only_panels()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Admin", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Open feedback", "Feedback's AdminOnly contributed tile must render for an Admin");
        html.Should().Contain("Recent activity", "Admin/Index's AdminOnly panel must render for an Admin");

        // The tag helper strips the attribute from elements it lets through, so one that keeps it was never bound.
        html.Should().NotContain("authorize-policy", "an unbound authorize-policy leaves the element visible to everyone");
    }

    /// <summary>
    /// The breadcrumb names the group and the item, and exactly one sidebar row and one tab are active.
    /// </summary>
    /// <remarks>
    /// Regression guard: a controller-only match lit up every item under <c>controller=Debug</c>.
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_breadcrumb_and_the_active_sidebar_link_name_one_page()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, group, item) in new[]
                 {
                     ("/Debug/Logs", "Debug", "Logs"),
                     ("/Debug/DbStats", "Debug", "DB stats"),
                 })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");
            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().Contain($"<span>{group}</span>", $"GET {url}'s breadcrumb must name its group");
            html.Should().Contain($"<span class=\"here\">{item}</span>",
                $"GET {url}'s breadcrumb must end on its own item");

            ActiveSidebarLink().Matches(html).Count.Should().Be(1, $"GET {url} must light exactly one sidebar row");
            ActiveTab().Matches(html).Count.Should().Be(1,
                $"GET {url} lit more than one tab — the ddfdb6c1 shape, where the active "
                + "match was on controller alone and every item under it went active together");
        }
    }

    /// <summary>
    /// The pill renders only when <c>AuthorizationPillFilter</c> set <c>ViewData["AuthPillRoles"]</c>, so a 200 proves
    /// nothing; the pill's own text is the marker.
    /// </summary>
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
    /// Every <c>Layout = "_AdminLayout"</c> resolves to exactly one file, in Shell's <c>Views/Shared</c>. Two copies
    /// would be worse than none: whichever the view engine found first would win, per request.
    /// </summary>
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
    /// Every <c>&lt;partial name="…"/&gt;</c> the two layouts render resolves to exactly one file. A partial left
    /// behind still compiles and returns 200; it just renders nothing.
    /// </summary>
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
    /// Shifts pages redirect when no burn is active, and a 302 proves nothing about chrome. Seeded through the
    /// section's verb because its Singleton view is warmed before the test body runs.
    /// </summary>
    private async Task SeedActiveBurnAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var eventId = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<IEventSettingsSeeding>().CreateActiveEventAsync(
            new EventSettingsInfo(
                Id: eventId,
                EventName: "Admin Chrome Test Burn",
                Year: 2026,
                TimeZoneId: "Europe/Madrid",
                GateOpeningDate: new LocalDate(2026, 7, 1),
                BuildStartOffset: -14,
                EventEndOffset: 6,
                StrikeEndOffset: 9,
                FirstCrewStartOffset: -25,
                SetupWeekStartOffset: -16,
                PreEventWeekStartOffset: -9,
                FinishingWeekendStartOffset: -4,
                EarlyEntryCapacity: new Dictionary<int, int>(),
                BarriosEarlyEntryAllocation: null,
                EarlyEntryClose: null));
        await scope.ServiceProvider.GetRequiredService<IShiftSeeding>()
            .SetShiftBrowsingOpenAsync(eventId, isOpen: true);
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
