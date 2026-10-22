using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Teams.Services;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders the Early Entry roster through the real app.</summary>
/// <remarks>
/// <para>
/// Only the table branch exercises anything: <c>TableModel</c>, <c>_Table</c> and <c>&lt;vc:human&gt;</c> all sit
/// behind <c>Model.Rows.Count != 0</c>. An unseeded GET has no <c>&lt;vc:</c> at all and passes a "no unrendered tag
/// helper" check while proving nothing, so the roster is seeded and the assertions are on the rendered row.
/// </para>
/// <para>
/// The section owns no tables, so seeding goes through Teams' real <c>IEarlyEntryProvider</c>. That also covers
/// discovery: a section outside Shell's dependency graph registers nothing while the build stays green.
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

        // Proves the view resolved, not just the route.
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

        html.Should().NotContain("-view-component", $"GET {RosterUrl} has a rewritten vc tag");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_roster()
    {
        // An internal controller in another assembly, guarded by a policy Shifts registers, not this section.
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

        // A fresh team: the flag flip below is a real write, and the seeded system teams are shared.
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
