using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Shifts.Authorization;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Authorization;

public sealed class IsAnyTeamManagerOrCoordinatorHandlerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IShiftService _shiftManagement = Substitute.For<IShiftService>();
    private readonly IsAnyTeamManagerOrCoordinatorHandler _handler;
    private readonly HttpContextAccessor _httpContext = new();

    public IsAnyTeamManagerOrCoordinatorHandlerTests()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        _handler = new IsAnyTeamManagerOrCoordinatorHandler(_shiftManagement, _users, _httpContext);
        _shiftManagement.GetCoordinatorTeamIdsAsync(Arg.Any<Guid>()).Returns([]);
    }

    [HumansTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task MembershipAccess_UsesResolvedAccountAndDeniesMissingAccounts(
        bool accountExists, bool survivorAuthorized)
    {
        var coordinatorId = Guid.NewGuid();
        var cookieId = survivorAuthorized ? Guid.NewGuid() : coordinatorId;
        var survivorId = survivorAuthorized ? coordinatorId : Guid.NewGuid();
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(accountExists
            ? UserInfo.Create(new User { Id = survivorId }, [], [], null)
            : null);
        _shiftManagement.GetCoordinatorTeamIdsAsync(coordinatorId, Arg.Any<CancellationToken>())
            .Returns([Guid.NewGuid()]);

        var allowed = await EvaluateAsync(CreateUserWithIdAndRole(cookieId, "SomeNonAdminRole"));

        allowed.Should().Be(accountExists && survivorAuthorized);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoordinatorLookup_PropagatesAbandonedRequests(bool atUser)
    {
        using var request = new CancellationTokenSource();
        _httpContext.HttpContext = new DefaultHttpContext { RequestAborted = request.Token };
        var userId = Guid.NewGuid();
        var observed = CancellationToken.None;
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var ct = call.Arg<CancellationToken>();
            if (atUser)
            {
                observed = ct;
                request.Cancel();
            }
            ct.ThrowIfCancellationRequested();
            return UserInfo.Create(new User { Id = userId }, [], [], null);
        });
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            if (!atUser) request.Cancel();
            observed.ThrowIfCancellationRequested();
            return new List<Guid> { Guid.NewGuid() };
        });

        Func<Task> authorize = () => EvaluateAsync(CreateUserWithIdAndRole(userId, "SomeNonAdminRole"));
        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
    }

    [HumansTheory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(ShiftsRoles.ShiftsNoInfo, true)]
    [InlineData(ShiftsRoles.ShiftsAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(TeamsRoles.TeamsAdmin, false)]
    public async Task ChecksCorrectRoles(string role, bool expected)
    {
        var result = await EvaluateAsync(CreateUserWithRole(role));
        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task AllowsUserWithCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([Guid.NewGuid()]);

        var result = await EvaluateAsync(CreateUserWithIdAndRole(userId, "SomeNonAdminRole"));

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task DeniesUserWithNoRoleAndNoCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([]);

        var result = await EvaluateAsync(CreateUserWithIdAndRole(userId, "SomeNonAdminRole"));

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task PrivilegedRole_ShortCircuitsWithoutCallingShiftService()
    {
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.Admin));

        result.Should().BeTrue();
        await _shiftManagement.DidNotReceive().GetCoordinatorTeamIdsAsync(Arg.Any<Guid>());
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user)
    {
        var requirement = new IsAnyTeamManagerOrCoordinatorRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal CreateUserWithRole(string role) =>
        CreateUserWithIdAndRole(Guid.NewGuid(), role);

    private static ClaimsPrincipal CreateUserWithIdAndRole(Guid userId, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "testuser@example.com"),
            new(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
