using Humans.GoogleIntegration.Contracts;
using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders Monitor's pages through the real app, as the standing form of the §15 step 12
/// check for the section's carve-out of <c>Humans.AuditLog</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// This is the only test that can catch the failure the carve is most likely to produce:
/// a section outside Shell's dependency graph. <c>SectionAssemblies()</c> walks
/// <c>DependencyContext</c>, so a missing <c>ProjectReference</c> in
/// <c>Humans.Web.csproj</c> means <c>Section.Register</c> never runs, the controller is
/// never discovered, and every route below 404s — while the solution builds and the whole
/// suite passes (G5-SECTION-TEMPLATE.md step 1, Guide's case).
/// </para>
/// <para>
/// The three actions moved here from <c>AuditLogController</c> because two of them inject
/// GoogleIntegration services and AuditLog is a *horizontal*: <c>peters-hard-rules.md</c>
/// forbids a horizontal from referencing a vertical section. The third came with them
/// because all three render the same <c>GoogleSync</c> view. So this asserts an internal
/// controller in a new assembly, routed by <c>SectionControllerFeatureProvider</c>, over
/// Base's <c>IAuditViewerService</c>, with its policy still in Shell's
/// <c>AuthorizationPolicyExtensions</c> (step 6's asymmetry).
/// </para>
/// </remarks>
public class MonitorPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task Google_sync_audit_for_a_human_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var url = $"/Monitor/Human/{adminId}";
        var response = await Client.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Google Sync Audit", $"GET {url} must render its own copy");

        // Monitor ships no resource set, so the raw-key probe does not apply. The probe that
        // replaces it is the <page-header> tag helper the view opens with: an unbound tag
        // helper neither throws nor degrades — it survives into the response as its own start
        // tag (step 12, Debug's case). This is what a missing @addTagHelper in the section's
        // own _ViewImports looks like.
        html.Should().NotContain("<page-header", $"GET {url} left <page-header> unbound");
        html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");
        html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Google_sync_audit_for_an_unknown_resource_is_a_404_not_a_500()
    {
        // Proves the route is registered *and* that ITeamResourceService resolves out of the
        // section's DI graph: an unregistered dependency is a 500 here, not a 404, and a
        // section missing from Shell's dependency graph is a 404 from the router rather than
        // from the action. The distinction matters — see the remarks above.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync($"/Monitor/Resource/{Guid.NewGuid()}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the action must run and report the resource missing");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Monitor_is_closed_to_a_non_privileged_human()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var volunteerId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync($"/Monitor/Human/{volunteerId}", ct);

        // Cookie authentication redirects an authenticated-but-unauthorized request to
        // Program.cs's AccessDeniedPath app-wide; HumanAdminBoardOrAdmin is not a 403 here.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the policy in Shell must still gate the section's controller");
    }
}
