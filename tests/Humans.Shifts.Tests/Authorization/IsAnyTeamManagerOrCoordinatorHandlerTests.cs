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
    private readonly IShiftService _shiftManagement = Substitute.For<IShiftService>();
    private readonly IsAnyTeamManagerOrCoordinatorHandler _handler;
    private readonly HttpContextAccessor _httpContext = new();

    public IsAnyTeamManagerOrCoordinatorHandlerTests()
    {
        _handler = new IsAnyTeamManagerOrCoordinatorHandler(_shiftManagement, _httpContext);
        _shiftManagement.GetCoordinatorTeamIdsAsync(Arg.Any<Guid>()).Returns([]);
    }

    [HumansFact]
    public async Task CoordinatorLookup_PropagatesAbandonedRequests()
    {
        using var request = new CancellationTokenSource();
        _httpContext.HttpContext = new DefaultHttpContext { RequestAborted = request.Token };
        var userId = Guid.NewGuid();
        var observed = CancellationToken.None;
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            request.Cancel();
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
