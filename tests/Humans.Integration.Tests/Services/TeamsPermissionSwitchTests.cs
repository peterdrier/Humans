using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Temporary, until departments move to Roles: Teams' old manage check (coordinates the team or its department)
/// against the new Teams.Manage and Teams.EarlyEntry checks, for today's coordinators on every active team. Delete
/// with Teams' temporary permission source.
/// </summary>
public class TeamsPermissionSwitchTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Manage_TodaysCoordinators_MatchesOldTeamCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        // The coordinator persona seeds a department it coordinates and a sub-team below it.
        var people = new[]
        {
            await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Coordinator),
            await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer),
        };

        using var scope = Factory.Services.CreateScope();
        var teams = scope.ServiceProvider.GetRequiredService<ITeamService>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        var activeTeams = (await teams.GetTeamsAsync(ct)).Values.Where(t => t.IsActive).ToList();
        var checkedHolding = false;

        foreach (var userId in people)
        {
            foreach (var team in activeTeams)
            {
                var coordinates = await teams.IsUserCoordinatorOfTeamAsync(team.Id, userId, ct);
                checkedHolding |= coordinates;
                (await permissions.HasAsync(userId, TeamsPermissions.Manage, TeamOwner.Of(team.Id), ct))
                    .Should().Be(coordinates, $"Teams.Manage on {team.Name}");
                (await permissions.HasAsync(userId, TeamsPermissions.EarlyEntry, TeamOwner.Of(team.Id), ct))
                    .Should().Be(coordinates, $"Teams.EarlyEntry on {team.Name}");
            }
        }

        checkedHolding.Should().BeTrue("the coordinator persona must coordinate a team, or the comparison proves nothing");
    }
}
