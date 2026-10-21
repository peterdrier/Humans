using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Auth.Controllers;
using Humans.Auth.Models;
using Humans.Auth.Services;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Auth.Tests.Controllers;

/// <summary>
/// The role-assignment admin pages served by Auth (Users decomposition commit 4): the roster,
/// assign and end. Pins the URLs, the gate, the per-role deny paths and the writes (whose
/// audit entries are the write service's, covered by <c>RoleAssignmentServiceTests</c>).
/// </summary>
public sealed class RoleAssignmentsControllerTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid TargetId = Guid.NewGuid();
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 20, 12, 0);

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IRoleAssignmentManagement _write = Substitute.For<IRoleAssignmentManagement>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public RoleAssignmentsControllerTests()
    {
        _clock.GetCurrentInstant().Returns(Now);
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>() + " {0}"));
        _localizer = localizer;
        GivenAuthorization(RoleNames.Board, succeeds: true);
    }

    private readonly IStringLocalizer<SharedResource> _localizer;

    private RoleAssignmentsController BuildController(CancellationToken aborted = default)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ActorId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "test"));
        var http = new DefaultHttpContext { User = principal, RequestAborted = aborted };
        return new RoleAssignmentsController(
            _users, _roles, _write, _clock, _authorization, NullLogger<RoleAssignmentsController>.Instance, _localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private void GivenKnownUsers()
    {
        _users.GetUserInfoAsync(ActorId, Arg.Any<CancellationToken>()).Returns(User(ActorId));
        _users.GetRawUserInfoAsync(TargetId, Arg.Any<CancellationToken>()).Returns(User(TargetId));
    }

    private static ValueTask<UserInfo?> User(Guid id) => new(new UserInfo(
        id, "Member", false, "en", null, Instant.FromUnixTimeSeconds(0),
        null, null, null, null, null, null, null,
        null, null, null, [], [], null));

    private void GivenAuthorization(string roleName, bool succeeds) =>
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), roleName, PolicyNames.RoleAssignmentManage)
            .Returns(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    [HumansFact]
    public async Task Roles_ForwardsFilterAndRendersTheRosterView()
    {
        using var source = new CancellationTokenSource();
        var summary = new RoleAssignmentSummarySnapshot(
            Guid.NewGuid(), TargetId, "m@example.org", "Member", RoleNames.Board,
            Now - Duration.FromDays(1), null, "n", ActorId, "Actor", Now - Duration.FromDays(1));
        _roles.GetFilteredAsync(RoleNames.Board, false, 2, 50, Now, source.Token)
            .Returns(([summary], 51));

        var result = await BuildController(source.Token).Roles(RoleNames.Board, showInactive: true, page: 2);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().BeNull();
        var model = view.Model.Should().BeOfType<AdminRoleAssignmentListViewModel>().Subject;
        model.RoleFilter.Should().Be(RoleNames.Board);
        model.ShowInactive.Should().BeTrue();
        model.TotalCount.Should().Be(51);
        model.RoleAssignments.Should().ContainSingle().Which.IsActive.Should().BeTrue();
        await _roles.Received(1).GetFilteredAsync(RoleNames.Board, false, 2, 50, Now, source.Token);
    }

    [HumansFact]
    public async Task AddRoleGet_ListsTheAssignableRoles_AndPreservesCancellation()
    {
        GivenKnownUsers();
        using var source = new CancellationTokenSource();

        var result = await BuildController(source.Token).AddRole(TargetId);

        var model = result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<CreateRoleAssignmentViewModel>().Subject;
        model.UserId.Should().Be(TargetId);
        model.AvailableRoles.Should().Equal(RoleChecks.GetAssignableRoles(BuildController().User));
        await _users.Received(1).GetRawUserInfoAsync(TargetId, source.Token);
    }

    [HumansFact]
    public async Task AddRoleGet_IsNotFound_ForAnUnknownMember()
    {
        (await BuildController().AddRole(Guid.NewGuid())).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task AddRolePost_AssignsAsTheSignedInActor_ThenRedirectsToTheMemberDetail()
    {
        GivenKnownUsers();
        _write.AssignRoleAsync(TargetId, RoleNames.Board, ActorId, "why", Arg.Any<CancellationToken>())
            .Returns(new RoleAssignmentResult(true));

        var result = await BuildController().AddRole(
            TargetId, new CreateRoleAssignmentViewModel { RoleName = RoleNames.Board, Notes = "why" });

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("AdminDetail");
        redirect.ControllerName.Should().Be("UsersAdmin");
        redirect.RouteValues!["id"].Should().Be(TargetId);
        await _write.Received(1).AssignRoleAsync(TargetId, RoleNames.Board, ActorId, "why", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddRolePost_IsForbidden_WhenTheRoleIsOutsideTheActorsAuthority()
    {
        GivenKnownUsers();
        GivenAuthorization(RoleNames.Admin, succeeds: false);

        var result = await BuildController().AddRole(
            TargetId, new CreateRoleAssignmentViewModel { RoleName = RoleNames.Admin });

        result.Should().BeOfType<ForbidResult>();
        await _write.DidNotReceive().AssignRoleAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddRolePost_RedisplaysTheForm_WhenNoRoleIsChosen()
    {
        GivenKnownUsers();
        var controller = BuildController();

        var result = await controller.AddRole(TargetId, new CreateRoleAssignmentViewModel { RoleName = " " });

        result.Should().BeOfType<ViewResult>();
        controller.ModelState.IsValid.Should().BeFalse();
        await _write.DidNotReceive().AssignRoleAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EndRole_EndsAsTheSignedInActor_ThenRedirectsToTheAssigneesDetail()
    {
        GivenKnownUsers();
        var roleId = Guid.NewGuid();
        _roles.GetByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new RoleAssignmentDetailSnapshot(TargetId, RoleNames.Board, "Member"));
        _write.EndRoleAsync(roleId, ActorId, "done", Arg.Any<CancellationToken>()).Returns(new RoleAssignmentResult(true));

        var result = await BuildController().EndRole(Guid.NewGuid(), roleId, "done");

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("AdminDetail");
        redirect.ControllerName.Should().Be("UsersAdmin");
        redirect.RouteValues!["id"].Should().Be(TargetId);
        await _write.Received(1).EndRoleAsync(roleId, ActorId, "done", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EndRole_IsNotFound_ForAnUnknownAssignment()
    {
        GivenKnownUsers();

        (await BuildController().EndRole(TargetId, Guid.NewGuid(), null)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task EndRole_IsNotFound_AndWritesNothing_WhenTheRoleIsOutsideTheActorsAuthority()
    {
        GivenKnownUsers();
        var roleId = Guid.NewGuid();
        _roles.GetByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new RoleAssignmentDetailSnapshot(TargetId, RoleNames.Admin, "Member"));
        GivenAuthorization(RoleNames.Admin, succeeds: false);

        var result = await BuildController().EndRole(TargetId, roleId, null);

        result.Should().BeOfType<NotFoundResult>();
        await _write.DidNotReceive().EndRoleAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void Routes_AreAuthUrls()
    {
        Template<HttpGetAttribute>(nameof(RoleAssignmentsController.Roles)).Should().Be("Auth/Admin/Roles");
        Template<HttpGetAttribute>(nameof(RoleAssignmentsController.AddRole)).Should().Be("Auth/Admin/{id:guid}/Roles/Add");
        Template<HttpPostAttribute>(nameof(RoleAssignmentsController.AddRole)).Should().Be("Auth/Admin/{id:guid}/Roles/Add");
        Template<HttpPostAttribute>(nameof(RoleAssignmentsController.EndRole)).Should().Be("Auth/Admin/{id:guid}/Roles/{roleId:guid}/End");
    }

    [HumansFact]
    public void Authorization_IsHumanAdminBoardOrAdmin_AndEveryPostIsAntiForgeryProtected()
    {
        typeof(RoleAssignmentsController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(PolicyNames.HumanAdminBoardOrAdmin);

        foreach (var post in typeof(RoleAssignmentsController).GetMethods()
                     .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null))
            post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull(post.Name);
    }

    private static string Template<TAttribute>(string action) where TAttribute : HttpMethodAttribute =>
        typeof(RoleAssignmentsController).GetMethods()
            .Where(m => string.Equals(m.Name, action, StringComparison.Ordinal) && m.GetCustomAttribute<TAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<TAttribute>()!.Template!)
            .Single();
}
