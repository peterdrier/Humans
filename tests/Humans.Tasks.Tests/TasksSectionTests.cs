using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Xunit;

namespace Humans.Tasks.Tests;

public sealed class TasksSectionTests
{
    private readonly Section _section = new();

    [HumansFact]
    public void TasksManage_IsDeclaredOnTeamsAndReachesSubTeams_AndTasksAdminHoldsItEverywhere()
    {
        var permission = ((ISectionPermissions)_section).Permissions.Should().ContainSingle().Which;

        permission.Key.Should().Be(TasksPermissions.Manage);
        permission.OwnerKinds.Should().Equal(TeamOwner.Kind);
        permission.TopLevelOnly.Should().BeFalse();
        permission.AdminRoles.Should().Equal(TasksSystemRoles.TasksAdmin);
        permission.Resource.Should().Be(typeof(TasksResource));
    }

    [HumansFact]
    public void TasksAdmin_IsDeclaredAsASystemRole() =>
        ((ISectionRoles)_section).Roles.Should().ContainSingle().Which.Name.Should().Be(TasksSystemRoles.TasksAdmin);

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(TasksSystemRoles.TasksAdmin, true)]
    [InlineData("Volunteer", false)]
    public void ReadPolicy_AdmitsTasksAdminBoardAndAdminOnly(string role, bool admitted)
    {
        var options = new AuthorizationOptions();
        ((ISectionPolicies)_section).AddPolicies(options);

        var allowed = options.GetPolicy(TasksPolicies.Read)!.Requirements
            .Should().ContainSingle().Which.Should().BeOfType<RolesAuthorizationRequirement>().Which.AllowedRoles;

        allowed.Contains(role, StringComparer.Ordinal).Should().Be(admitted);
    }
}
