using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Google.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Temporary, until departments move to Roles: Google's old resource-manager check (coordinates the team's
/// department) against the new permission check on that department, for today's coordinators on every active team.
/// Delete with Teams' temporary permission source.
/// </summary>
public class GooglePermissionSwitchTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Todays_coordinators_manage_resources_exactly_where_they_did_before()
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
                var departmentId = team.ParentTeamId ?? team.Id;
                var coordinates = await teams.IsUserCoordinatorOfTeamAsync(departmentId, userId, ct);
                checkedHolding |= coordinates;
                (await permissions.HasAsync(userId, GooglePermissions.ManageResources,
                        new Owner(TeamOwner.Kind, departmentId), ct))
                    .Should().Be(coordinates, $"Google.ManageResources for {team.Name}");
            }
        }

        checkedHolding.Should().BeTrue("the coordinator persona must coordinate a team, or the comparison proves nothing");
    }
}
