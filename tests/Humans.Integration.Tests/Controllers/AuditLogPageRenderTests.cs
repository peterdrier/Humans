using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

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
/// The other half this test exists for is the section's <em>controller</em>. It stayed on
/// Base's <c>IAuditViewerService</c> — the name-resolving read path could not follow the
/// section out, because it injects Users', Teams' and Google Integration's read surfaces and
/// a horizontal section may not reference a vertical. So the page is an internal controller in
/// one assembly, routed by <c>SectionControllerFeatureProvider</c>, over an orchestrator in
/// another, with its policy still in Shell's <c>AuthorizationPolicyExtensions</c> (step 6's
/// asymmetry). One authorized GET and one unauthorized GET is what proves those three halves
/// still meet.
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

    [HumansFact(Timeout = 120000)]
    public async Task The_shared_audit_log_view_component_still_renders_from_Humans_UI()
    {
        // AuditLogViewComponent and AuditEvent stayed in Humans.UI / Humans.Application when
        // the section moved, so every <vc:audit-log> call site was untouched. This asserts
        // that from the busiest of them — a component that failed to resolve would render as
        // inert markup rather than throwing.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync($"/Users/Admin/{adminId}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().NotContain("<vc:audit-log", "the audit history widget must still bind");
    }
}
