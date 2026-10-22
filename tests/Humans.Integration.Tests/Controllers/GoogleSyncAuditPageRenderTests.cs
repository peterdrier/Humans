using System.Net;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders GoogleIntegration's sync-audit pages through the real app.</summary>
/// <remarks>
/// A missing <c>ProjectReference</c> in <c>Humans.Web.csproj</c> 404s every route here while the build passes. The
/// <c>-view-component</c> check stays because the static tag scanner keys on <c>&lt;vc:</c> and can't see a ReSharper
/// rename.
/// </remarks>
public class GoogleSyncAuditPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
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

        // English-only page, so the probe is <page-header>: unbound, it survives as its own start tag.
        html.Should().NotContain("<page-header", $"GET {url} left <page-header> unbound");
        html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
    }

    /// <summary>A seeded marker proves the component resolved and rendered its own data.</summary>
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
        // An unregistered dependency is a 500 from the action; a section missing from Shell's graph is a 404 from the
        // router.
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

        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the policy in Shell must still gate the section's controller");
    }
}
