using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using NSubstitute;

namespace Humans.Teams.Tests.Infrastructure;

/// <summary>
/// Stands in for Permissions with today's Teams rule, so service tests keep setting up coordinators and admins:
/// Admin, Teams Admin, or a coordinator of the team or its department holds <c>Teams.Manage</c>.
/// </summary>
internal static class TestTeamPermissions
{
    public static IPermissionService Create(ISystemRoleService roles, Func<ITeamService> teams)
    {
        var permissions = Substitute.For<IPermissionService>();
        permissions.HasAsync(Arg.Any<Guid>(), TeamsPermissions.Manage, Arg.Any<Owner>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var userId = call.ArgAt<Guid>(0);
                return await roles.IsUserAdminAsync(userId)
                    || await roles.IsUserTeamsAdminAsync(userId)
                    || await teams().IsUserCoordinatorOfTeamAsync(call.ArgAt<Owner>(2).Id, userId);
            });
        return permissions;
    }
}
