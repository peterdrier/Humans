using Humans.Users.Contracts;
using Humans.EarlyEntry.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Teams.Authorization;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Teams.Tests.Authorization;

public sealed class TeamAuthorizationHandlerTests
{
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly TeamAuthorizationHandler _handler;

    private static readonly Guid ParentTeamId = Guid.NewGuid();
    private static readonly Guid ChildTeamId = Guid.NewGuid();
    private static readonly Guid CoordinatorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public TeamAuthorizationHandlerTests()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], profile: null)));
        _handler = new TeamAuthorizationHandler(_teamService, _users, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(CreateTeamMap());
    }

    [HumansTheory]
    [InlineData("coordinator", true, true)]
    [InlineData("child", true, true)]
    [InlineData("other", true, false)]
    [InlineData("coordinator", false, false)]
    [InlineData("child", false, false)]
    public async Task CoordinatorAccess_ResolvesMergedAndMissingAccounts(string team, bool merged, bool expected)
    {
        var cookieId = merged ? Guid.NewGuid() : UserId;
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(merged
                ? UserInfo.Create(new User { Id = UserId }, [], [], profile: null) : null));

        var allowed = await EvaluateAsync(CreateUserWithId(cookieId), CreateTeam(team), TeamOperationRequirement.ManageCoordinators);

        allowed.Should().Be(expected);
        if (!merged) _teamService.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task CoordinatorAccess_CancelsAccountResolutionBeforeTeamReads()
    {
        using var request = new CancellationTokenSource();
        await request.CancelAsync();
        _users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = UserId }, [], [], profile: null));
        });
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var handler = new TeamAuthorizationHandler(_teamService, _users, http);
        var context = new AuthorizationHandlerContext([TeamOperationRequirement.ManageCoordinators],
            CreateUserWithId(UserId), CreateTeam("coordinator"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(context));
        _teamService.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AuthorizationRead_PropagatesAbandonedRequests()
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var observed = CancellationToken.None;
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            request.Cancel();
            observed.ThrowIfCancellationRequested();
            return CreateTeamMap();
        });
        var handler = new TeamAuthorizationHandler(_teamService, _users, http);
        var context = new AuthorizationHandlerContext([TeamOperationRequirement.ManageCoordinators],
            CreateUser("coordinator", Guid.NewGuid()), CreateTeam("coordinator"));
        Func<Task> authorize = () => handler.HandleAsync(context);
        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
        context.HasSucceeded.Should().BeFalse();
    }

    public static TheoryData<string, string, bool> TeamAuthorizationCases => new()
    {
        { "admin", "other", true },
        { "teams-admin", "other", true },
        { "board", "other", false },
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
        var user = CreateUserWithRoles(EarlyEntryRoles.EarlyEntryAdmin);
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
            "teams-admin" => CreateUserWithRoles(TeamsRoles.TeamsAdmin),
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
