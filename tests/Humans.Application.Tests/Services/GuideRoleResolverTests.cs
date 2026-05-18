using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class GuideRoleResolverTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 21, 12, 0));
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();

    public GuideRoleResolverTests()
    {
        // Default: empty team cache.
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
    }

    private ClaimsPrincipal PrincipalWithRoles(Guid? userId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var identity = new ClaimsIdentity(claims, authenticationType: userId is null ? null : "test");
        return new ClaimsPrincipal(identity);
    }

    private GuideRoleResolver CreateResolver() => new(_teamService);

    private TeamInfo BuildTeam(Guid teamId, params TeamMemberInfo[] members) => new(
        Id: teamId,
        Name: "T",
        Description: null,
        Slug: "t",
        IsActive: true,
        IsSystemTeam: false,
        SystemTeamType: SystemTeamType.None,
        RequiresApproval: false,
        IsPublicPage: false,
        IsHidden: false,
        IsPromotedToDirectory: false,
        CreatedAt: _clock.GetCurrentInstant(),
        Members: members.ToList());

    private TeamMemberInfo Member(Guid userId, TeamMemberRole role) => new(
        TeamMemberId: Guid.NewGuid(),
        UserId: userId,
        DisplayName: "u",
        Email: null,
        ProfilePictureUrl: null,
        Role: role,
        JoinedAt: _clock.GetCurrentInstant());

    private void StubTeams(params TeamInfo[] teams)
    {
        var byId = teams.ToDictionary(t => t.Id);
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(byId);
    }

    [HumansFact]
    public async Task Resolve_Anonymous_ReturnsAnonymousContext()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        result.IsAuthenticated.Should().BeFalse();
        result.IsTeamCoordinator.Should().BeFalse();
        result.SystemRoles.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Resolve_AuthWithAdminRole_ReportsSystemRoles()
    {
        var resolver = CreateResolver();
        var user = PrincipalWithRoles(Guid.NewGuid(), RoleNames.Admin, RoleNames.Board);

        var result = await resolver.ResolveAsync(user);

        result.IsAuthenticated.Should().BeTrue();
        result.SystemRoles.Should().Contain([RoleNames.Admin, RoleNames.Board]);
    }

    [HumansFact]
    public async Task Resolve_ActiveTeamCoordinator_IsTeamCoordinatorTrue()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        StubTeams(BuildTeam(teamId, Member(userId, TeamMemberRole.Coordinator)));

        var resolver = CreateResolver();
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeTrue();
    }

    [HumansFact]
    public async Task Resolve_FormerTeamCoordinator_IsTeamCoordinatorFalse()
    {
        // TeamInfo.Members in the cache only contains active (LeftAt is null) memberships,
        // so a former coordinator simply does not appear in the cached members list.
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        StubTeams(BuildTeam(teamId)); // no members — former coordinator pruned

        var resolver = CreateResolver();
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeFalse();
    }

    [HumansFact]
    public async Task Resolve_MemberButNotCoordinator_IsTeamCoordinatorFalse()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        StubTeams(BuildTeam(teamId, Member(userId, TeamMemberRole.Member)));

        var resolver = CreateResolver();
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeFalse();
    }

    [HumansFact]
    public async Task Resolve_CoordinatorOnOneTeamMemberOnAnother_IsTeamCoordinatorTrue()
    {
        var userId = Guid.NewGuid();
        StubTeams(
            BuildTeam(Guid.NewGuid(), Member(userId, TeamMemberRole.Member)),
            BuildTeam(Guid.NewGuid(), Member(userId, TeamMemberRole.Coordinator)));

        var resolver = CreateResolver();
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeTrue();
    }
}
