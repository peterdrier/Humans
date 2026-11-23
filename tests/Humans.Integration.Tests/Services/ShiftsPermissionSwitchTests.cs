using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Integration.Tests.Infrastructure;
using Humans.Permissions.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Temporary, until departments move to Roles: Shifts' old coordinator check (coordinates the team or its parent)
/// against the new permission checks, for today's coordinators on every active team. Delete with Teams' temporary
/// permission source.
/// </summary>
public class ShiftsPermissionSwitchTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Todays_coordinators_manage_and_approve_exactly_where_they_did_before()
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
            var coordinated = await teams.GetUserCoordinatedTeamIdsAsync(userId, ct);
            checkedHolding |= coordinated.Count > 0;

            foreach (var team in activeTeams)
            {
                var before = coordinated.Contains(team.Id)
                             || (team.ParentTeamId is { } parentId && coordinated.Contains(parentId));
                var owner = new Owner(TeamOwner.Kind, team.Id);

                (await permissions.HasAsync(userId, ShiftsPermissions.Manage, owner, ct))
                    .Should().Be(before, $"Shifts.Manage on {team.Name}");
                (await permissions.HasAsync(userId, ShiftsPermissions.Approve, owner, ct))
                    .Should().Be(before, $"Shifts.Approve on {team.Name}");
            }

            (await permissions.GetOwnersAsync(userId, ShiftsPermissions.Manage, ct)).Count
                .Should().Be(activeTeams.Count(t => coordinated.Contains(t.Id)
                    || (t.ParentTeamId is { } p && coordinated.Contains(p))));
        }

        checkedHolding.Should().BeTrue("the coordinator persona must coordinate a team, or the comparison proves nothing");
    }
}
