using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Teams.Services;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the Early Entry roster through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.EarlyEntry</c>
/// (nobodies-collective/Humans#866, G5 lane 4b-2b).
/// </summary>
/// <remarks>
/// <para>
/// The page has an empty branch and a table branch, and <b>only the table branch exercises
/// anything the move can break</b>: <c>TableModel</c>, the <c>_Table</c> partial and the
/// <c>&lt;vc:human&gt;</c> tag helper all live behind <c>Model.Rows.Count != 0</c>. An
/// unseeded GET returns 200 with a body containing no <c>&lt;vc:</c> at all, which satisfies a
/// naive "no unrendered tag helper" assertion while proving nothing — the failure lane 4b-1
/// recorded as finding 37's sibling. So the roster is seeded first, and the assertions are on
/// the rendered row.
/// </para>
/// <para>
/// Seeding goes through Teams' own provider path rather than a stub: the section owns no
/// tables, so the only way a row reaches the roster is a real <c>IEarlyEntryProvider</c>
/// contributing one. That also covers the DI wiring the move rearranged — Shell's
/// <c>AddEarlyEntrySection()</c> became <c>Humans.EarlyEntry.Section.Register</c>, discovered
/// rather than called, and a section outside Shell's dependency graph registers nothing while
/// the build stays green.
/// </para>
/// </remarks>
public class EarlyEntryPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string RosterUrl = "/Shifts/Admin/EarlyEntry";

    [HumansFact(Timeout = 120000)]
    public async Task The_roster_renders_a_seeded_grant_with_its_table_and_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        await SeedTeamEarlyEntryGrantAsync(userId, new LocalDate(2026, 8, 20), "Effigy build", ct);

        var response = await Client.GetAsync(RosterUrl, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {RosterUrl} must render");

        var html = await response.Content.ReadAsStringAsync(ct);

        // The section's own copy — proves the moved view resolved, not just the route.
        html.Should().Contain("Early Entry Roster");
        html.Should().Contain("Everyone granted early entry");

        // The table branch. Without the seed above, none of these three exist on the page.
        html.Should().Contain("Effigy build",
            because: "the seeded grant's source label must reach the Source(s) column");
        html.Should().Contain("Legal name",
            because: "the TableModel column headers must render through the _Table partial, "
                   + "which resolves by name across application parts");
        html.Should().NotContain("No early entry grants",
            because: "the empty branch would render none of the markup this test guards");

        // A view component element the section's _ViewImports failed to bind renders as
        // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
        html.Should().NotContain("<vc:", $"GET {RosterUrl} left a view-component tag unrendered");
        html.Should().NotContain("-view-component", $"GET {RosterUrl} has a rewritten vc tag");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_roster()
    {
        // The section's negative access rule. Worth pinning at the move because the controller
        // is now an internal type in another assembly routed through
        // SectionControllerFeatureProvider, while ShiftDashboardAccess stayed in Shell's
        // AuthorizationPolicyExtensions (design §15 step 6's asymmetry).
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync(RosterUrl, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {RosterUrl} must be gated");
        response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
            because: $"GET {RosterUrl} must be denied, not sent to sign in");
    }

    private async Task SeedTeamEarlyEntryGrantAsync(
        Guid userId, LocalDate entryDate, string projectName, CancellationToken ct)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var write = scope.ServiceProvider.GetRequiredService<ITeamManagementService>();

        // A fresh team rather than one of the dev-login seeded system teams: the flag flip
        // below is a real write, and the seeded set is shared with every other fixture.
        var team = await write.CreateTeamAsync(
            "Build Crew", "EE fixture", requiresApproval: false, cancellationToken: ct);

        // Only a team with EarlyEntryEnabled contributes grants, and AddEarlyEntryGrantAsync
        // rejects a disabled one.
        await write.UpdateTeamAsync(
            team.Id, team.Name, team.Description, team.RequiresApproval, isActive: true,
            earlyEntryEnabled: true, cancellationToken: ct);

        await write.AddEarlyEntryGrantAsync(team.Id, userId, entryDate, projectName, userId, ct);
    }
}
