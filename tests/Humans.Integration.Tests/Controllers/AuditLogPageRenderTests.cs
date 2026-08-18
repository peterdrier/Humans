using System.Net;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Audit Log page through the real app, as the standing form of the §15
/// step 12 check for the section's move into <c>src/Sections/Humans.AuditLog</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>,
/// so "the page loads" is not the assertion. AuditLog carries no resource set at all — its
/// pages are admin-only English — so the usual raw-key probe does not apply and the probe
/// that replaces it is the <c>&lt;page-header&gt;</c> tag helper both pages open with: an
/// unbound tag helper neither throws nor degrades, it survives into the response as its own
/// start tag (G5-SECTION-TEMPLATE.md step 12, Debug's case).
/// </para>
/// <para>
/// The other half this test exists for is the section's <em>controller</em>. Since G5 lane
/// 4b-2h (nobodies-collective/Humans#866) it runs over <c>IAuditViewerService</c> in its own
/// assembly, but its policy is still in Shell's <c>AuthorizationPolicyExtensions</c> (step 6's
/// asymmetry): an internal controller routed by <c>SectionControllerFeatureProvider</c>, gated
/// from Shell. One authorized GET and one unauthorized GET is what proves those halves meet.
/// </para>
/// <para>
/// The three Google-sync pages this file used to cover — <c>CheckDriveActivity</c>,
/// <c>Resource/{id}</c> and <c>Human/{id}</c> — now live in <c>Humans.Monitor</c> and are
/// covered by <c>MonitorPageRenderTests</c>.
/// </para>
/// </remarks>
public class AuditLogPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static string[] Pages =>
    [
        "/AuditLog",
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_audit_log_page_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var url in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain("Audit Log", $"GET {url} must render its own copy");

            // An unbound Humans.UI tag helper survives as its own start tag: 200, correct
            // -looking source, missing widget. This is the section's only such probe,
            // because it ships no resource set to assert raw keys against.
            html.Should().NotContain("<page-header", $"GET {url} left <page-header> unbound");

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup, which the browser silently drops.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Audit_log_is_closed_to_a_non_privileged_human()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/AuditLog", ct);

        // Cookie authentication redirects an authenticated-but-unauthorized request to
        // Program.cs's AccessDeniedPath app-wide; BoardOrAdmin is not a 403 here.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the BoardOrAdmin policy in Shell must still gate the section's controller");
    }

    /// <summary>
    /// Every assembly that renders <c>&lt;vc:audit-log&gt;</c>, proved by seeded content
    /// rather than by absence of a literal tag.
    /// </summary>
    /// <remarks>
    /// <c>AuditLogViewComponent</c> moved from <c>Humans.UI</c> into <c>Humans.AuditLog</c> in
    /// G5 lane 4b-2h (nobodies-collective/Humans#866), so each consuming assembly now needs
    /// <c>@addTagHelper *, Humans.AuditLog</c> in its own <c>_ViewImports.cshtml</c>. Missing
    /// that line is silent: the element ships as inert literal markup with a green build.
    /// <para>
    /// Asserting only <c>NotContain("&lt;vc:")</c> proves nothing — a page whose audit widget
    /// never ran passes it too. So this seeds a real audit row whose description is a unique
    /// marker and asserts the marker reaches the HTML, which can only happen if the tag helper
    /// bound, the component resolved <c>IAuditViewerService</c> from the section's own
    /// registration, and <c>Views/Shared/Components/AuditLog/*</c> resolved from the section
    /// assembly.
    /// </para>
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_audit_log_view_component_binds_and_renders_in_every_consuming_assembly()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // ShiftSignupBailed is one of the few actions whose Description is rendered as a tail
        // (AuditEventTextualizer.ShouldRenderDescriptionTail), which is what lets a unique
        // marker travel all the way from the row to the HTML.
        var marker = $"vc-binding-probe-{Guid.NewGuid():N}";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
            await auditLog.LogAsync(
                AuditAction.ShiftSignupBailed,
                entityType: "User",
                entityId: adminId,
                description: marker,
                actorUserId: adminId);
        }

        // Humans.Users — UsersAdmin/AdminDetail, the busiest call site — and Humans.Web —
        // WidgetGallery, Shell's own. Both render the widget for the signed-in human with no
        // extra fixtures, so both can carry the marker assertion.
        foreach (var url in new[] { $"/Users/Admin/{adminId}", "/WidgetGallery" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(marker,
                $"GET {url}: the seeded audit row must reach the page — an unbound <vc:audit-log> renders nothing");
            html.Should().NotContain("<vc:audit-log",
                $"GET {url}: the audit history widget must bind, not ship as literal markup");
            html.Should().NotContain("audit-log-view-component",
                $"GET {url}: a ReSharper-rewritten vc tag is inert markup too");
        }
    }

    /// <summary>
    /// Every <c>&lt;vc:audit-log&gt;</c> call site in the tree sits under a
    /// <c>_ViewImports.cshtml</c> chain that binds <c>Humans.AuditLog</c>'s tag helpers.
    /// </summary>
    /// <remarks>
    /// The render test above covers the two call sites that need no fixtures. The other three
    /// — Teams' <c>TeamAdmin/Members</c>, Store's <c>StoreAdmin/CatalogEdit</c> and Tickets'
    /// <c>TicketTransferAdmin/Detail</c> — render the widget only for a seeded team, product or
    /// transfer request, so this covers them structurally instead, and covers whatever call
    /// site is added next. A Roslyn analyzer cannot see <c>.cshtml</c>, which is why this is a
    /// test (peters-hard-rules.md prefers analyzers where they can reach).
    /// </remarks>
    [HumansFact]
    public void Every_audit_log_call_site_sits_under_a_view_imports_that_binds_it()
    {
        const string Directive = "@addTagHelper *, Humans.AuditLog";
        var src = Path.Combine(FindRepoRoot(), "src");

        var callSites = Directory
            .EnumerateFiles(src, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("<vc:audit-log", StringComparison.Ordinal))
            .ToList();

        callSites.Should().NotBeEmpty("the component would otherwise be dead");

        var unbound = callSites.Where(f => !BindsAuditLog(f, src, Directive)).ToList();

        unbound.Should().BeEmpty(
            "a <vc:audit-log> whose assembly never opens Humans.AuditLog's tag helpers ships as "
            + "inert literal markup with a green build and no runtime error");
    }

    // Razor applies every _ViewImports.cshtml from the project root down to the view's folder.
    private static bool BindsAuditLog(string viewPath, string srcRoot, string directive)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(viewPath)!);
             dir is not null && dir.FullName.StartsWith(srcRoot, StringComparison.Ordinal);
             dir = dir.Parent)
        {
            var imports = Path.Combine(dir.FullName, "_ViewImports.cshtml");
            if (File.Exists(imports)
                && File.ReadAllText(imports).Contains(directive, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
