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
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Teams.Tests.Controllers;

public class TeamAdminControllerMembersTests
{
    [HumansTheory]
    [InlineData("permission", "foreign")]
    [InlineData("permission", "missing")]
    [InlineData("permission", "own")]
    [InlineData("permission", "denied")]
    [InlineData("restriction", "foreign")]
    [InlineData("restriction", "missing")]
    [InlineData("restriction", "own")]
    [InlineData("restriction", "denied")]
    [InlineData("unlink", "foreign")]
    [InlineData("unlink", "missing")]
    [InlineData("unlink", "own")]
    [InlineData("unlink", "denied")]
    [InlineData("sync", "foreign")]
    [InlineData("sync", "missing")]
    [InlineData("sync", "own")]
    [InlineData("sync", "denied")]
    public async Task ResourceMutation_RequiresResourceFromAuthorizedTeam(string action, string scenario)
    {
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserServiceRead>();
        var team = new Team { Id = Guid.NewGuid(), Name = "Team", Slug = "team" };
        var resources = Substitute.For<ITeamResourceService>();
        var sync = Substitute.For<IGoogleSyncService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        teams.GetTeamEntityBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(team);
        resources.CanManageTeamResourcesAsync(team.Id, userId, Arg.Any<CancellationToken>())
            .Returns(!string.Equals(scenario, "denied", StringComparison.Ordinal));
        if (!string.Equals(scenario, "missing", StringComparison.Ordinal))
            resources.GetResourceByIdAsync(resourceId, Arg.Any<CancellationToken>()).Returns(
                new GoogleResourceSnapshot(resourceId,
                    string.Equals(scenario, "foreign", StringComparison.Ordinal) ? Guid.NewGuid() : team.Id,
                    "google-id", "Folder", GoogleResourceType.DriveFolder, null));
        resources.SetRestrictInheritedAccessWithResultAsync(resourceId, false, CancellationToken.None)
            .Returns(TeamResourceMutationResult.Success());
        sync.SyncSingleResourceAsync(resourceId, SyncAction.Execute, CancellationToken.None)
            .Returns(new ResourceSyncDiff { ResourceId = resourceId });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new TeamAdminController(teams, resources, sync, users,
            Substitute.For<IEmailProvisioningService>(), Substitute.For<IAuthorizationService>(),
            NullLogger<TeamAdminController>.Instance, localizer,
            Substitute.For<ITicketServiceRead>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        var result = action switch
        {
            "permission" => await controller.UpdatePermissionLevel(team.Slug, resourceId, DrivePermissionLevel.Viewer),
            "restriction" => await controller.ToggleRestrictInheritedAccess(team.Slug, resourceId, false),
            "unlink" => await controller.UnlinkResource(team.Slug, resourceId),
            _ => await controller.SyncResource(team.Slug, resourceId)
        };
        if (string.Equals(scenario, "denied", StringComparison.Ordinal)) Assert.IsType<ForbidResult>(result);
        else if (!string.Equals(scenario, "own", StringComparison.Ordinal)) Assert.IsType<NotFoundResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        var mutationCalls = resources.ReceivedCalls().Where(c =>
            !string.Equals(c.GetMethodInfo().Name, nameof(ITeamResourceService.CanManageTeamResourcesAsync), StringComparison.Ordinal)
            && !string.Equals(c.GetMethodInfo().Name, nameof(ITeamResourceService.GetResourceByIdAsync), StringComparison.Ordinal));
        if (string.Equals(scenario, "own", StringComparison.Ordinal))
        {
            if (string.Equals(action, "sync", StringComparison.Ordinal)) Assert.Single(sync.ReceivedCalls());
            else Assert.Single(mutationCalls);
        }
        else
        {
            Assert.Empty(mutationCalls);
            Assert.Empty(sync.ReceivedCalls());
        }
    }

    [HumansTheory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task InheritedAccess_InvalidBinding_DoesNotDispatch(bool allowed, bool invalidBinding)
    {
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserServiceRead>();
        var team = new Team { Id = Guid.NewGuid(), Name = "Team", Slug = "team" };
        var resources = Substitute.For<ITeamResourceService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        teams.GetTeamEntityBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(team);
        resources.GetResourceByIdAsync(resourceId, Arg.Any<CancellationToken>()).Returns(
            new GoogleResourceSnapshot(resourceId, team.Id, "google-id", "Folder", GoogleResourceType.DriveFolder, null));
        resources.CanManageTeamResourcesAsync(team.Id, userId, Arg.Any<CancellationToken>()).Returns(allowed);
        resources.SetRestrictInheritedAccessWithResultAsync(resourceId, false, CancellationToken.None)
            .Returns(TeamResourceMutationResult.Success());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var controller = new TeamAdminController(teams, resources, Substitute.For<IGoogleSyncService>(), users,
            Substitute.For<IEmailProvisioningService>(), Substitute.For<IAuthorizationService>(),
            NullLogger<TeamAdminController>.Instance, Substitute.For<IStringLocalizer<TeamsResource>>(),
            Substitute.For<ITicketServiceRead>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        if (invalidBinding) controller.ModelState.AddModelError("restrict", "Not a boolean.");

        var result = await controller.ToggleRestrictInheritedAccess(team.Slug, resourceId, false);

        if (!allowed) Assert.IsType<ForbidResult>(result);
        else if (invalidBinding) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (!allowed || invalidBinding)
            await resources.DidNotReceive().SetRestrictInheritedAccessWithResultAsync(
                Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        else await resources.Received(1).SetRestrictInheritedAccessWithResultAsync(resourceId, false, CancellationToken.None);
    }

    [HumansTheory]
    [InlineData("reject", false)]
    [InlineData("remove", false)]
    [InlineData("add", false)]
    [InlineData("reject", true)]
    [InlineData("remove", true)]
    [InlineData("add", true)]
    public async Task ExpectedRejection_PreservesFeedbackAndContextWithoutStack_UnexpectedFailurePropagates(
        string action, bool unexpected)
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, instant, []);
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = actorId }, [], [], [], null, [])));
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        const string reason = "The requested membership change is not permitted";
        Exception failure = unexpected ? new IOException("Database unavailable") : new InvalidOperationException(reason);
        teams.RejectJoinRequestAsync(targetId, actorId, "Reason").Returns(Task.FromException(failure));
        teams.RemoveMemberAsync(team.Id, targetId, actorId).Returns(unexpected
            ? Task.FromException<TeamMemberRemovalResult>(new InvalidOperationException("Private dependency failure"))
            : Task.FromResult(new TeamMemberRemovalResult("Teams_RemoveMember_Permission")));
        teams.AddMemberToTeamAsync(team.Id, targetId, actorId).Returns(Task.FromException<TeamMember>(failure));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var logger = Substitute.For<ILogger<TeamAdminController>>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test"))
        };
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>();
        localizer["Teams_RemoveMember_Permission"].Returns(new LocalizedString("Teams_RemoveMember_Permission", reason));
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(),
            Substitute.For<IGoogleSyncService>(), users, Substitute.For<IEmailProvisioningService>(), authorization,
            logger, localizer, Substitute.For<ITicketServiceRead>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        Task<IActionResult> ActAsync() => action switch
        {
            "reject" => controller.RejectRequest(team.Slug, targetId, new ApproveRejectRequestModel { Notes = "Reason" }),
            "remove" => controller.RemoveMember(team.Slug, targetId),
            _ => controller.AddMember(team.Slug, new AddMemberModel { UserId = targetId })
        };
        if (unexpected)
        {
            Func<Task> act = async () => await ActAsync();
            if (string.Equals(action, "remove", StringComparison.Ordinal)) await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Private dependency failure");
            else await act.Should().ThrowAsync<IOException>();
            logger.ReceivedCalls().Should().BeEmpty();
            return;
        }

        var redirect = (await ActAsync()).Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(nameof(TeamAdminController.Members));
        redirect.RouteValues!["slug"].Should().Be(team.Slug);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(string.Equals(action, "remove", StringComparison.Ordinal) ? "Teams_RemoveMember_Permission" : reason).And.Contain(actorId.ToString())
            .And.Contain(team.Id.ToString()).And.Contain(targetId.ToString());
    }

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
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = aborted.Token,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")),
                }
            },
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
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "Test")),
                }
            },
        };
        var first = (TeamMembersViewModel)((ViewResult)await controller.Members(team.Slug)).Model!;
        first.Members.Should().ContainSingle();

        var result = (TeamMembersViewModel)((ViewResult)await controller.Members(team.Slug, page)).Model!;
        result.Members.Should().HaveCount(expectedCount);
        result.PageNumber.Should().Be(expectedPage);
        result.TotalCount.Should().Be(1);
    }
}
