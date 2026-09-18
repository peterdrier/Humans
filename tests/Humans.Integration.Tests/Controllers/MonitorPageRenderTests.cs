using System.Net;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders GoogleIntegration's sync-audit pages through the real app.
/// </summary>
/// <remarks>
/// <para>
/// This is the only test that can catch the failure the carve is most likely to produce:
/// a section outside Shell's dependency graph. <c>SectionAssemblies()</c> walks
/// <c>DependencyContext</c>, so a missing <c>ProjectReference</c> in
/// <c>Humans.Web.csproj</c> means <c>Section.Register</c> never runs, the controller is
/// never discovered, and every route below 404s — while the solution builds and the whole
/// suite passes (G5-SECTION-TEMPLATE.md step 1, Guide's case). Note the `-view-component`
/// assertion below is deliberate and survives nobodies-collective/Humans#1434's sweep of the
/// `NotContain("&lt;vc:")` probes: the static scanner keys on the literal `&lt;vc:` substring,
/// so it cannot see a ReSharper rename that rewrites the tag out of the source.
/// </para>
/// <para>
/// These pages live beside GoogleIntegration's sync-log component. This asserts the internal
/// controller is routed by <c>SectionControllerFeatureProvider</c>, the component renders, and
/// the policies registered in Shell still apply.
/// </para>
/// </remarks>
public class MonitorPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task Google_sync_audit_for_a_human_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var url = $"/Google/Human/{adminId}";
        var response = await Client.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Google Sync Audit", $"GET {url} must render its own copy");

        // The admin page uses English copy, so the raw-key probe does not apply. The probe that
        // replaces it is the <page-header> tag helper the view opens with: an unbound tag
        // helper neither throws nor degrades — it survives into the response as its own start
        // tag (step 12, Debug's case). This is what a missing @addTagHelper in the section's
        // own _ViewImports looks like.
        html.Should().NotContain("<page-header", $"GET {url} left <page-header> unbound");
        html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
    }

    /// <summary>
    /// The sync rows reach the page, proved by seeded content rather than by absence of a
    /// literal tag.
    /// </summary>
    /// <remarks>
    /// The page emits <c>&lt;vc:google-sync-log&gt;</c>; asserting a seeded marker proves the
    /// component resolved and rendered its section-owned data.
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task Google_sync_rows_reach_the_page_through_the_sync_log_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var marker = $"sync-log-probe-{Guid.NewGuid():N}";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var syncLog = scope.ServiceProvider.GetRequiredService<IGoogleSyncLogService>();
            await syncLog.LogAsync(
                GoogleSyncLogAction.AccessGranted,
                resourceId: Guid.NewGuid(),
                description: marker,
                jobName: "ProbeJob",
                userEmail: "dev-admin@localhost",
                role: "reader",
                source: GoogleSyncSource.ManualSync,
                success: true,
                userId: adminId,
                ct: ct);
        }

        var url = $"/Google/Human/{adminId}";
        var response = await Client.GetAsync(url, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain(marker,
            $"GET {url}: the seeded sync row must reach the page — an unbound <vc:google-sync-log> renders nothing");
        html.Should().Contain("reader", $"GET {url} must render the sync log's Role column");
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

        var response = await Client.GetAsync($"/Google/Resource/{Guid.NewGuid()}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the action must run and report the resource missing");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Google_sync_audit_is_closed_to_a_non_privileged_human()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var volunteerId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync($"/Google/Human/{volunteerId}", ct);

        // Cookie authentication redirects an authenticated-but-unauthorized request to
        // Program.cs's AccessDeniedPath app-wide; HumanAdminBoardOrAdmin is not a 403 here.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the policy in Shell must still gate the section's controller");
    }
}
