using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Budget.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Temporary, until departments move to Roles: Budget's old coordinator check (coordinates a department, reaching its
/// sub-teams) against the new permission check, for today's coordinators on every active team. Delete with Teams'
/// temporary permission source.
/// </summary>
public class BudgetPermissionSwitchTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Todays_department_coordinators_edit_budgets_exactly_where_they_did_before()
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
            var before = (await teams.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, ct)).ToHashSet();
            checkedHolding |= before.Count > 0;

            foreach (var team in activeTeams)
            {
                (await permissions.HasAsync(userId, BudgetPermissions.Edit, new Owner(TeamOwner.Kind, team.Id), ct))
                    .Should().Be(before.Contains(team.Id), $"Budget.Edit on {team.Name}");
            }

            (await permissions.GetOwnersAsync(userId, BudgetPermissions.Edit, ct)).Count
                .Should().Be(activeTeams.Count(t => before.Contains(t.Id)));
        }

        checkedHolding.Should().BeTrue("the coordinator persona must coordinate a department, or the comparison proves nothing");
    }
}
