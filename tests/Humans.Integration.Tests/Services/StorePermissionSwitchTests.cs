using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Integration.Tests.Infrastructure;
using Humans.Permissions.Contracts;
using Humans.Store.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Temporary, until departments move to Roles: Store's old team-order check (a management-role holder on a
/// department) against the new permission check, for today's coordinators on every active department. The new check
/// also counts a Coordinator row without a management role; the hourly sync demotes those, so outside that window the
/// two sets match. The seeded coordinator persona holds only the row. Delete with Teams' temporary permission source.
/// </summary>
public class StorePermissionSwitchTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task TeamOrders_TodaysCoordinators_MatchesOldDepartmentCheck()
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
        var departments = (await teams.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && !t.IsSystemTeam && t.ParentTeamId is null)
            .ToList();
        var checkedHolding = false;

        foreach (var userId in people)
        {
            foreach (var team in departments)
            {
                var manages = team.ManagementRoleHolderUserIds?.Contains(userId) == true
                    || await teams.IsUserCoordinatorOfTeamAsync(team.Id, userId, ct);
                checkedHolding |= manages;
                (await permissions.HasAsync(userId, StorePermissions.TeamOrders, new Owner(TeamOwner.Kind, team.Id), ct))
                    .Should().Be(manages, $"Store.TeamOrders on {team.Name}");
            }
        }

        checkedHolding.Should().BeTrue("the coordinator persona must manage a department, or the comparison proves nothing");
    }
}
