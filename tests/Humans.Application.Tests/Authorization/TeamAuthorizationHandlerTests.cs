using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

public sealed class TeamAuthorizationHandlerTests
{
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly TeamAuthorizationHandler _handler;

    private static readonly Guid CoordinatorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public TeamAuthorizationHandlerTests()
    {
        _handler = new TeamAuthorizationHandler(_teamService);
        _teamService.IsUserCoordinatorOfTeamAsync(CoordinatorTeamId, UserId).Returns(true);
        _teamService.IsUserCoordinatorOfTeamAsync(OtherTeamId, UserId).Returns(false);
    }

    public static TheoryData<string, string, bool> TeamAuthorizationCases => new()
    {
        { "admin", "other", true },
        { "teams-admin", "other", true },
        { "board", "other", true },
        { "coordinator", "coordinator", true },
        { "coordinator", "other", false },
        { "regular", "coordinator", false },
        { "anonymous", "coordinator", false },
        { "invalid-id", "coordinator", false },
    };

    [HumansTheory]
    [MemberData(nameof(TeamAuthorizationCases))]
    public async Task Team_management_authorization_matches_expected_scenarios(
        string userKind,
        string teamKind,
        bool expected)
    {
        var regularUserId = Guid.NewGuid();
        _teamService.IsUserCoordinatorOfTeamAsync(CoordinatorTeamId, regularUserId).Returns(false);

        var user = CreateUser(userKind, regularUserId);
        var team = CreateTeam(teamKind);

        var result = await EvaluateAsync(user, team);

        result.Should().Be(expected);
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, Team resource)
    {
        var requirement = TeamOperationRequirement.ManageCoordinators;
        var context = new AuthorizationHandlerContext([requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static Team CreateTeam(string kind) =>
        new()
        {
            Id = kind switch
            {
                "coordinator" => CoordinatorTeamId,
                "other" => OtherTeamId,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            }
        };

    private static ClaimsPrincipal CreateUser(string kind, Guid regularUserId) =>
        kind switch
        {
            "admin" => CreateUserWithRoles(RoleNames.Admin),
            "teams-admin" => CreateUserWithRoles(RoleNames.TeamsAdmin),
            "board" => CreateUserWithRoles(RoleNames.Board),
            "coordinator" => CreateUserWithId(UserId),
            "regular" => CreateUserWithId(regularUserId),
            "anonymous" => new ClaimsPrincipal(new ClaimsIdentity()),
            "invalid-id" => new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "not-a-guid"),
                new Claim(ClaimTypes.Name, "test@example.com")
            ], "TestAuth")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

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

    private static ClaimsPrincipal CreateUserWithId(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "coordinator@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
