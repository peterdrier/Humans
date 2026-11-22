using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Expenses.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Temporary, until departments move to Roles: Expenses' old coordinator check (coordinates the team or any team
/// above it) against the new permission check, for today's coordinators on every active team. Delete with Teams'
/// temporary permission source.
/// </summary>
public class ExpensesPermissionSwitchTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Todays_coordinators_endorse_claims_exactly_where_they_did_before()
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
            var before = 0;
            foreach (var team in activeTeams)
            {
                var coordinates = await teams.IsUserCoordinatorOfTeamAsync(team.Id, userId, ct);
                before += coordinates ? 1 : 0;
                (await permissions.HasAsync(userId, ExpensesPermissions.Endorse, new Owner(TeamOwner.Kind, team.Id), ct))
                    .Should().Be(coordinates, $"Expenses.Endorse on {team.Name}");
            }

            checkedHolding |= before > 0;
            (await permissions.GetOwnersAsync(userId, ExpensesPermissions.Endorse, ct)).Count.Should().Be(before);
        }

        checkedHolding.Should().BeTrue("the coordinator persona must coordinate a team, or the comparison proves nothing");
    }
}
