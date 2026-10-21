using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Authorization;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.GoogleIntegration.Tests.Authorization;

/// <summary>
/// The team-resources gate: TeamsAdmin/Board/Admin manage any team; everyone else needs the
/// resource-manager check on the team's department; a sub-team never checks itself.
/// </summary>
public sealed class TeamResourceAuthorizationHandlerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task TeamsAdminBoardOrAdmin_Succeeds_WithoutTheResourceManagerCheck(string role)
    {
        var allowed = await EvaluateAsync(Principal(_userId, role), Team(Guid.NewGuid(), parentTeamId: null));

        allowed.Should().BeTrue();
        await _resources.DidNotReceive().CanManageTeamResourcesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Department_FollowsTheResourceManagerCheck(bool manager)
    {
        var team = Team(Guid.NewGuid(), parentTeamId: null);
        _resources.CanManageTeamResourcesAsync(team.Id, _userId, Arg.Any<CancellationToken>()).Returns(manager);

        var allowed = await EvaluateAsync(Principal(_userId), team);

        allowed.Should().Be(manager);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubTeam_IsCheckedAtItsDepartment_NeverItself(bool departmentManager)
    {
        var departmentId = Guid.NewGuid();
        var subTeam = Team(Guid.NewGuid(), parentTeamId: departmentId);
        _resources.CanManageTeamResourcesAsync(departmentId, _userId, Arg.Any<CancellationToken>()).Returns(departmentManager);
        _resources.CanManageTeamResourcesAsync(subTeam.Id, _userId, Arg.Any<CancellationToken>()).Returns(true);

        var allowed = await EvaluateAsync(Principal(_userId), subTeam);

        allowed.Should().Be(departmentManager);
        await _resources.DidNotReceive().CanManageTeamResourcesAsync(
            subTeam.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MissingUserId_IsDenied()
    {
        _resources.CanManageTeamResourcesAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([], "test"));

        var allowed = await EvaluateAsync(anonymous, Team(Guid.NewGuid(), parentTeamId: null));

        allowed.Should().BeFalse();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, TeamInfo team)
    {
        var handler = new TeamResourceAuthorizationHandler(_resources, new HttpContextAccessor());
        var context = new AuthorizationHandlerContext([TeamResourceManageRequirement.Instance], user, team);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal Principal(Guid userId, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TeamInfo Team(Guid id, Guid? parentTeamId) =>
        new(id, "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, [], ParentTeamId: parentTeamId);
}
