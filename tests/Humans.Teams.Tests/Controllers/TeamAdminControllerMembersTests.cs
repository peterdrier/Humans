using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Controllers;
using Humans.Teams.Models;
using Humans.Teams.Domain;
using Humans.Teams.Services;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Teams.Tests.Controllers;

public class TeamAdminControllerMembersTests
{
    [HumansTheory]
    [InlineData("viewer")]
    [InlineData("team")]
    [InlineData("permission")]
    [InlineData("resources")]
    [InlineData("account")]
    public async Task Resources_ObservesRequestCancellationAtEveryRead(string boundary)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var userId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserServiceRead>();
        var team = new Team { Id = Guid.NewGuid(), Name = "Team", Slug = "team" };
        var resources = Substitute.For<ITeamResourceService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, [])));
        teams.GetTeamEntityBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(team);
        resources.CanManageTeamResourcesAsync(team.Id, userId, Arg.Any<CancellationToken>()).Returns(true);
        resources.GetTeamResourcesAsync(team.Id, Arg.Any<CancellationToken>()).Returns([]);
        resources.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>()).Returns("service@example.com");
        switch (boundary)
        {
            case "viewer":
                users.GetUserInfoAsync(userId, aborted.Token).Returns(ValueTask.FromCanceled<UserInfo?>(aborted.Token));
                break;
            case "team":
                teams.GetTeamEntityBySlugAsync(team.Slug, aborted.Token).Returns(Task.FromCanceled<Team?>(aborted.Token));
                break;
            case "permission":
                resources.CanManageTeamResourcesAsync(team.Id, userId, aborted.Token).Returns(Task.FromCanceled<bool>(aborted.Token));
                break;
            case "resources":
                resources.GetTeamResourcesAsync(team.Id, aborted.Token)
                    .Returns(Task.FromCanceled<IReadOnlyList<GoogleResourceSnapshot>>(aborted.Token));
                break;
            case "account":
                resources.GetServiceAccountEmailAsync(aborted.Token).Returns(Task.FromCanceled<string>(aborted.Token));
                break;
        }
        var controller = new TeamAdminController(teams, resources, Substitute.For<IGoogleSyncService>(), users,
            Substitute.For<IEmailProvisioningService>(), Substitute.For<IAuthorizationService>(),
            NullLogger<TeamAdminController>.Instance, Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<ITicketServiceRead>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestAborted = aborted.Token,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")),
            } },
        };
        var act = () => controller.Resources(team.Slug);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(int.MaxValue, 0, int.MaxValue)]
    [InlineData(int.MinValue, 1, 1)]
    [InlineData(0, 1, 1)]
    public async Task Members_HandlesOutOfRangePagesWithoutWrapping(int page, int expectedCount, int expectedPage)
    {
        var userId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserServiceRead>();
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, instant,
            [new TeamMemberInfo(Guid.NewGuid(), userId, "Human", null, null, TeamMemberRole.Member, instant)]);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        teams.GetPendingRequestsForTeamAsync(team.Id).Returns([]);
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, [])));
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var resources = Substitute.For<ITeamResourceService>();
        resources.GetTeamResourcesAsync(team.Id, Arg.Any<CancellationToken>()).Returns([]);
        var controller = new TeamAdminController(teams, resources, Substitute.For<IGoogleSyncService>(), users,
            Substitute.For<IEmailProvisioningService>(), authorization, NullLogger<TeamAdminController>.Instance,
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<ITicketServiceRead>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "Test")),
            } },
        };
        var first = (TeamMembersViewModel)((ViewResult)await controller.Members(team.Slug)).Model!;
        first.Members.Should().ContainSingle();

        var result = (TeamMembersViewModel)((ViewResult)await controller.Members(team.Slug, page)).Model!;
        result.Members.Should().HaveCount(expectedCount);
        result.PageNumber.Should().Be(expectedPage);
        result.TotalCount.Should().Be(1);
    }
}
