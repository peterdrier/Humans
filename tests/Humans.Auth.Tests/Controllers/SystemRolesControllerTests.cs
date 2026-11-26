using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Authorization;
using Humans.Auth.Contracts;
using Humans.Auth.Tests.Infrastructure;
using Humans.Auth.Controllers;
using Humans.Auth.Models;
using Humans.Auth.Services;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Teams.Contracts;
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
/// The system-role admin pages served by Auth: the roster,
/// assign and end. Pins the URLs, the gate, the per-role deny paths and the writes (whose
/// audit entries are the write service's, covered by <c>SystemRoleServiceTests</c>).
/// </summary>
public sealed class SystemRolesControllerTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid TargetId = Guid.NewGuid();
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 20, 12, 0);

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ISystemRoleService _roles = Substitute.For<ISystemRoleService>();
    private readonly ISystemRoleManagement _write = Substitute.For<ISystemRoleManagement>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly RoleCatalog _catalog = TestRoles.Catalog();

    public SystemRolesControllerTests()
    {
        _clock.GetCurrentInstant().Returns(Now);
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>() + " {0}"));
        _localizer = localizer;
        GivenAuthorization(RoleNames.Board, succeeds: true);
    }

    private readonly IStringLocalizer<SharedResource> _localizer;

    private SystemRolesController BuildController(CancellationToken aborted = default)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ActorId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "test"));
        var http = new DefaultHttpContext { User = principal, RequestAborted = aborted };
        return new SystemRolesController(
            _users, _roles, _write, _clock, _authorization, _catalog, NullLogger<SystemRolesController>.Instance, _localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private void GivenKnownUsers()
    {
        _users.GetUserInfoAsync(ActorId, Arg.Any<CancellationToken>()).Returns(UserAsync(ActorId));
        _users.GetRawUserInfoAsync(TargetId, Arg.Any<CancellationToken>()).Returns(UserAsync(TargetId));
    }

    private static ValueTask<UserInfo?> UserAsync(Guid id) => new(new UserInfo(
        id, "Member", false, "en", null, Instant.FromUnixTimeSeconds(0),
        null, null, null, null, null, null,
        null, null, null, [], [], null));

    private void GivenAuthorization(string roleName, bool succeeds) =>
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), roleName, PolicyNames.SystemRoleManage)
            .Returns(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    [HumansFact]
    public async Task Roles_RendersFilteredRoster()
    {
        using var source = new CancellationTokenSource();
        var summary = new SystemRoleSummarySnapshot(
            Guid.NewGuid(), TargetId, "m@example.org", "Member", RoleNames.Board,
            Now - Duration.FromDays(1), null, "n", ActorId, "Actor");
        _roles.GetFilteredAsync(RoleNames.Board, false, 2, 50, Now, source.Token)
            .Returns(([summary], 51));

        var result = await BuildController(source.Token).Roles(RoleNames.Board, showInactive: true, page: 2);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().BeNull();
        var model = view.Model.Should().BeOfType<AdminSystemRoleListViewModel>().Subject;
        model.RoleFilter.Should().Be(RoleNames.Board);
        model.ShowInactive.Should().BeTrue();
        model.TotalCount.Should().Be(51);
        model.SystemRoles.Should().ContainSingle().Which.IsActive.Should().BeTrue();
        await _roles.Received(1).GetFilteredAsync(RoleNames.Board, false, 2, 50, Now, source.Token);
    }

    [HumansFact]
    public async Task AddRoleGet_ListsAssignableRoles()
    {
        GivenKnownUsers();
        using var source = new CancellationTokenSource();

        var result = await BuildController(source.Token).AddRole(TargetId);

        var model = result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<CreateSystemRoleViewModel>().Subject;
        model.UserId.Should().Be(TargetId);
        model.AvailableRoles.Should().Equal(SystemRoleRules.Assignable(BuildController().User, _catalog));
        await _users.Received(1).GetRawUserInfoAsync(TargetId, source.Token);
    }

    [HumansFact]
    public async Task AddRoleGet_IsNotFound_ForAnUnknownMember()
    {
        (await BuildController().AddRole(Guid.NewGuid())).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task AddRole_UnavailableTarget_ReturnsNotFound()
    {
        GivenKnownUsers();
        GivenAuthorization(TeamsRoles.TeamsAdmin, succeeds: true);
        _write.AssignRoleAsync(TargetId, TeamsRoles.TeamsAdmin, ActorId, null, Arg.Any<CancellationToken>())
            .Returns(new SystemRoleResult(false, "UserUnavailable"));
        var controller = BuildController();

        var result = await controller.AddRole(TargetId, new CreateSystemRoleViewModel { RoleName = TeamsRoles.TeamsAdmin });

        result.Should().BeOfType<NotFoundResult>();
        controller.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddRolePost_AssignsAsSignedInActor()
    {
        GivenKnownUsers();
        GivenAuthorization(TeamsRoles.TeamsAdmin, succeeds: true);
        _write.AssignRoleAsync(TargetId, TeamsRoles.TeamsAdmin, ActorId, "why", Arg.Any<CancellationToken>())
            .Returns(new SystemRoleResult(true));

        var result = await BuildController().AddRole(
            TargetId, new CreateSystemRoleViewModel { RoleName = TeamsRoles.TeamsAdmin, Notes = "why" });

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("AdminDetail");
        redirect.ControllerName.Should().Be("UsersAdmin");
        redirect.RouteValues!["id"].Should().Be(TargetId);
        await _write.Received(1).AssignRoleAsync(TargetId, TeamsRoles.TeamsAdmin, ActorId, "why", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Board_IsManagedInGovernance()
    {
        GivenKnownUsers();
        var seatId = Guid.NewGuid();
        _write.GetByIdAsync(seatId, Arg.Any<CancellationToken>())
            .Returns(new SystemRoleDetailSnapshot(TargetId, RoleNames.Board, "Member"));

        SystemRoleRules.Assignable(BuildController().User, _catalog).Select(r => r.Name)
            .Should().NotContain(RoleNames.Board);
        (await BuildController().AddRole(TargetId, new CreateSystemRoleViewModel { RoleName = RoleNames.Board }))
            .Should().BeOfType<RedirectToActionResult>();
        (await BuildController().RevokeRole(TargetId, seatId, null)).Should().BeOfType<RedirectToActionResult>();

        await _write.DidNotReceiveWithAnyArgs().AssignRoleAsync(default, default!, default, default);
        await _write.DidNotReceiveWithAnyArgs().RevokeRoleAsync(default, default, default);
    }

    [HumansFact]
    public async Task AddRolePost_OutsideAuthority_IsForbidden()
    {
        GivenKnownUsers();
        GivenAuthorization(RoleNames.Admin, succeeds: false);

        var result = await BuildController().AddRole(
            TargetId, new CreateSystemRoleViewModel { RoleName = RoleNames.Admin });

        result.Should().BeOfType<ForbidResult>();
        await _write.DidNotReceive().AssignRoleAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddRolePost_NoRole_RedisplaysForm()
    {
        GivenKnownUsers();
        var controller = BuildController();

        var result = await controller.AddRole(TargetId, new CreateSystemRoleViewModel { RoleName = " " });

        result.Should().BeOfType<ViewResult>();
        controller.ModelState.IsValid.Should().BeFalse();
        await _write.DidNotReceive().AssignRoleAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RevokeRole_RevokesAsSignedInActor()
    {
        GivenKnownUsers();
        var roleId = Guid.NewGuid();
        _write.GetByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new SystemRoleDetailSnapshot(TargetId, TeamsRoles.TeamsAdmin, "Member"));
        GivenAuthorization(TeamsRoles.TeamsAdmin, succeeds: true);
        _write.RevokeRoleAsync(roleId, ActorId, "done", Arg.Any<CancellationToken>()).Returns(new SystemRoleResult(true));

        var result = await BuildController().RevokeRole(Guid.NewGuid(), roleId, "done");

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("AdminDetail");
        redirect.ControllerName.Should().Be("UsersAdmin");
        redirect.RouteValues!["id"].Should().Be(TargetId);
        await _write.Received(1).RevokeRoleAsync(roleId, ActorId, "done", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EndRole_IsNotFound_ForAnUnknownAssignment()
    {
        GivenKnownUsers();

        (await BuildController().RevokeRole(TargetId, Guid.NewGuid(), null)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task RevokeRole_OutsideAuthority_IsNotFound()
    {
        GivenKnownUsers();
        var roleId = Guid.NewGuid();
        _write.GetByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new SystemRoleDetailSnapshot(TargetId, RoleNames.Admin, "Member"));
        GivenAuthorization(RoleNames.Admin, succeeds: false);

        var result = await BuildController().RevokeRole(TargetId, roleId, null);

        result.Should().BeOfType<NotFoundResult>();
        await _write.DidNotReceive().RevokeRoleAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task Mutation_CancelledLookup_Stops(bool endRole, bool actorLookup)
    {
        GivenKnownUsers();
        using var request = new CancellationTokenSource();
        var roleId = Guid.NewGuid();
        _write.GetByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (endRole && !actorLookup)
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new SystemRoleDetailSnapshot(TargetId, TeamsRoles.TeamsAdmin, "Member");
            });
        _users.GetRawUserInfoAsync(TargetId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!endRole && !actorLookup)
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return UserAsync(TargetId);
            });
        _users.GetUserInfoAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (actorLookup)
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return UserAsync(ActorId);
            });
        _write.AssignRoleAsync(TargetId, TeamsRoles.TeamsAdmin, ActorId, null, Arg.Any<CancellationToken>())
            .Returns(new SystemRoleResult(true));
        _write.RevokeRoleAsync(roleId, ActorId, null, Arg.Any<CancellationToken>())
            .Returns(new SystemRoleResult(true));
        var controller = BuildController(request.Token);
        await request.CancelAsync();
        Func<Task<IActionResult>> invoke = endRole
            ? () => controller.RevokeRole(TargetId, roleId, null)
            : () => controller.AddRole(TargetId, new CreateSystemRoleViewModel { RoleName = TeamsRoles.TeamsAdmin });

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        _write.ReceivedCalls()
            .Where(call => call.GetMethodInfo().DeclaringType == typeof(ISystemRoleWrite))
            .Should().BeEmpty();
        _authorization.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public void Routes_AreAuthUrls()
    {
        Template<HttpGetAttribute>(nameof(SystemRolesController.Roles)).Should().Be("Auth/Admin/Roles");
        Template<HttpGetAttribute>(nameof(SystemRolesController.AddRole)).Should().Be("Auth/Admin/{id:guid}/Roles/Add");
        Template<HttpPostAttribute>(nameof(SystemRolesController.AddRole)).Should().Be("Auth/Admin/{id:guid}/Roles/Add");
        Template<HttpPostAttribute>(nameof(SystemRolesController.RevokeRole)).Should().Be("Auth/Admin/{id:guid}/Roles/{roleId:guid}/Revoke");
    }

    [HumansFact]
    public void Authorization_BoardReads_AdminsWrite()
    {
        typeof(SystemRolesController).GetCustomAttribute<AuthorizeAttribute>(inherit: false)!
            .Policy.Should().Be(UsersPolicies.Read);

        // Board passes the class gate but SystemRoleRules lets it manage nothing, so the add/end actions narrow
        // to the principals that can: otherwise Board lands on an empty add-role form and 404s on every End.
        foreach (var action in typeof(SystemRolesController).GetMethods()
                     .Where(m => m.Name is nameof(SystemRolesController.AddRole) or nameof(SystemRolesController.RevokeRole)))
            action.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(UsersPolicies.Manage, action.ToString());
        typeof(SystemRolesController).GetMethod(nameof(SystemRolesController.Roles))!
            .GetCustomAttribute<AuthorizeAttribute>().Should().BeNull("the roster stays readable to Board");

        foreach (var post in typeof(SystemRolesController).GetMethods()
                     .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null))
            post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull(post.Name);
    }

    private static string Template<TAttribute>(string action) where TAttribute : HttpMethodAttribute =>
        typeof(SystemRolesController).GetMethods()
            .Where(m => string.Equals(m.Name, action, StringComparison.Ordinal) && m.GetCustomAttribute<TAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<TAttribute>()!.Template!)
            .Single();
}
