using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

public sealed class TeamAuthorizationHandlerTests
{
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly TeamAuthorizationHandler _handler;

    private static readonly Guid ParentTeamId = Guid.NewGuid();
    private static readonly Guid ChildTeamId = Guid.NewGuid();
    private static readonly Guid CoordinatorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public TeamAuthorizationHandlerTests()
    {
        _handler = new TeamAuthorizationHandler(_teamService);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(CreateTeamMap());
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
        var user = CreateUser(userKind, regularUserId);
        var team = CreateTeam(teamKind);

        var result = await EvaluateAsync(user, team, TeamOperationRequirement.ManageCoordinators);

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task EETeamAdmin_can_manage_early_entry_on_any_team_but_not_coordinators()
    {
        var user = CreateUserWithRoles(RoleNames.EETeamAdmin);
        var team = CreateTeam("other");

        var canManageEarlyEntry = await EvaluateAsync(user, team, TeamOperationRequirement.ManageEarlyEntry);
        var canManageCoordinators = await EvaluateAsync(user, team, TeamOperationRequirement.ManageCoordinators);

        canManageEarlyEntry.Should().BeTrue();
        canManageCoordinators.Should().BeFalse();
    }

    [HumansFact]
    public async Task Coordinator_can_manage_early_entry_on_their_own_team()
    {
        var user = CreateUserWithId(UserId);
        var team = CreateTeam("coordinator");

        var result = await EvaluateAsync(user, team, TeamOperationRequirement.ManageEarlyEntry);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Parent_coordinator_can_manage_child_team()
    {
        var user = CreateUserWithId(UserId);
        var team = CreateTeam("child");

        var result = await EvaluateAsync(user, team, TeamOperationRequirement.ManageCoordinators);

        result.Should().BeTrue();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, TeamInfo resource, TeamOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static TeamInfo CreateTeam(string kind) =>
        new(
            Id: kind switch
            {
                "parent" => ParentTeamId,
                "child" => ChildTeamId,
                "coordinator" => CoordinatorTeamId,
                "other" => OtherTeamId,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            },
            Name: kind,
            Description: null,
            Slug: kind,
            IsActive: true,
            IsSystemTeam: false,
            SystemTeamType: SystemTeamType.None,
            RequiresApproval: false,
            IsPublicPage: false,
            IsHidden: false,
            IsPromotedToDirectory: false,
            CreatedAt: Instant.MinValue,
            Members: string.Equals(kind, "coordinator", StringComparison.Ordinal)
                || string.Equals(kind, "parent", StringComparison.Ordinal)
                ? [new TeamMemberInfo(Guid.NewGuid(), UserId, "Coordinator", null, null, TeamMemberRole.Coordinator, Instant.MinValue)]
                : [],
            ParentTeamId: string.Equals(kind, "child", StringComparison.Ordinal) ? ParentTeamId : null);

    private static IReadOnlyDictionary<Guid, TeamInfo> CreateTeamMap()
    {
        var teams = new[] { CreateTeam("parent"), CreateTeam("child"), CreateTeam("coordinator"), CreateTeam("other") };
        return teams.ToDictionary(team => team.Id);
    }

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
