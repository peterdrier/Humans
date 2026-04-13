using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for TeamAuthorizationHandler — resource-based authorization for team management.
/// Tests cover: Admin override, TeamsAdmin override, Board override, team coordinator access,
/// denial for non-coordinators, and edge cases.
/// </summary>
public sealed class TeamAuthorizationHandlerTests
{
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly TeamAuthorizationHandler _handler;

    private static readonly Guid CoordinatorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public TeamAuthorizationHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_teamService);
        var serviceProvider = services.BuildServiceProvider();
        _handler = new TeamAuthorizationHandler(serviceProvider);

        _teamService.IsUserCoordinatorOfTeamAsync(CoordinatorTeamId, UserId)
            .Returns(true);
        _teamService.IsUserCoordinatorOfTeamAsync(OtherTeamId, UserId)
            .Returns(false);
    }

    // --- Admin override ---

    [Fact]
    public async Task Admin_CanManageAnyTeam()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var team = CreateTeam(OtherTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeTrue();
    }

    // --- TeamsAdmin override ---

    [Fact]
    public async Task TeamsAdmin_CanManageAnyTeam()
    {
        var user = CreateUserWithRoles(RoleNames.TeamsAdmin);
        var team = CreateTeam(OtherTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeTrue();
    }

    // --- Board override ---

    [Fact]
    public async Task Board_CanManageAnyTeam()
    {
        var user = CreateUserWithRoles(RoleNames.Board);
        var team = CreateTeam(OtherTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeTrue();
    }

    // --- Team coordinator access ---

    [Fact]
    public async Task Coordinator_CanManageOwnTeam()
    {
        var user = CreateUser(UserId);
        var team = CreateTeam(CoordinatorTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Coordinator_CannotManageOtherTeam()
    {
        var user = CreateUser(UserId);
        var team = CreateTeam(OtherTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeFalse();
    }

    // --- Denial cases ---

    [Fact]
    public async Task RegularUser_DeniedOnAnyTeam()
    {
        var regularUserId = Guid.NewGuid();
        _teamService.IsUserCoordinatorOfTeamAsync(CoordinatorTeamId, regularUserId)
            .Returns(false);
        var user = CreateUser(regularUserId);
        var team = CreateTeam(CoordinatorTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeFalse();
    }

    // --- Edge cases ---

    [Fact]
    public async Task UnauthenticatedUser_Denied()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var team = CreateTeam(CoordinatorTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task UserWithInvalidIdClaim_Denied()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "not-a-guid"),
            new(ClaimTypes.Name, "test@example.com")
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var team = CreateTeam(CoordinatorTeamId);

        var result = await EvaluateAsync(user, team);

        result.Should().BeFalse();
    }

    // --- Helpers ---

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, Team resource)
    {
        var requirement = TeamOperationRequirement.ManageCoordinators;
        var context = new AuthorizationHandlerContext(
            [requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static Team CreateTeam(Guid teamId)
    {
        return new Team
        {
            Id = teamId
        };
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "admin@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal CreateUser(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "coordinator@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
