using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Guide.Services;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Guide.Tests.Services;

public class GuideRoleResolverTests
{
    public enum MembershipCase
    {
        SurvivorCoordinates,
        SurvivorLeadsCamp,
        AccountMissing
    }

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 21, 12, 0));
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ICampLeadDirectory _campLeads = Substitute.For<ICampLeadDirectory>();

    public GuideRoleResolverTests()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], profile: null));
        // Default: empty team cache, leads no camp.
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _campLeads
            .IsLeadAnywhereAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
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

    private GuideRoleResolver CreateResolver() => new(_teamService, _campLeads, _users, GuideTestRoles.Map);

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

    [HumansTheory]
    [InlineData(MembershipCase.SurvivorCoordinates)]
    [InlineData(MembershipCase.SurvivorLeadsCamp)]
    [InlineData(MembershipCase.AccountMissing)]
    public async Task Resolve_MembershipRolesUseTheResolvedAccount(MembershipCase kind)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var missing = kind == MembershipCase.AccountMissing;
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>())
            .Returns(missing ? null : UserInfo.Create(new User { Id = survivorId }, [], [], profile: null));
        var membershipId = missing ? cookieId : survivorId;
        if (kind != MembershipCase.SurvivorLeadsCamp)
            StubTeams(BuildTeam(Guid.NewGuid(), Member(membershipId, TeamMemberRole.Coordinator)));
        if (kind != MembershipCase.SurvivorCoordinates)
            _campLeads.IsLeadAnywhereAsync(membershipId, Arg.Any<CancellationToken>()).Returns(true);
        var ct = TestContext.Current.CancellationToken;

        var result = await CreateResolver().ResolveAsync(PrincipalWithRoles(cookieId, RoleNames.Board), ct);

        result.IsAuthenticated.Should().BeTrue();
        result.SystemRoles.Should().Contain(RoleNames.Board);
        result.IsTeamCoordinator.Should().Be(kind == MembershipCase.SurvivorCoordinates);
        result.IsCampLead.Should().Be(kind == MembershipCase.SurvivorLeadsCamp);
        await _users.Received(1).GetUserInfoAsync(cookieId, ct);
        if (missing)
        {
            _teamService.ReceivedCalls().Should().BeEmpty();
            _campLeads.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansFact]
    public async Task Resolve_AccountReadObservesCancellationBeforeMembershipReads()
    {
        var userId = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        var observed = CancellationToken.None;
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            request.Cancel();
            observed.ThrowIfCancellationRequested();
            return UserInfo.Create(new User { Id = userId }, [], [], profile: null);
        });
        Func<Task> resolve = () => CreateResolver().ResolveAsync(PrincipalWithRoles(userId), request.Token);

        await resolve.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
        _teamService.ReceivedCalls().Should().BeEmpty();
        _campLeads.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Resolve_Anonymous_ReturnsAnonymousContext()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity()), TestContext.Current.CancellationToken);

        result.IsAuthenticated.Should().BeFalse();
        result.IsTeamCoordinator.Should().BeFalse();
        result.IsCampLead.Should().BeFalse();
        result.SystemRoles.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Resolve_ActiveCampLead_IsCampLeadTrue()
    {
        var userId = Guid.NewGuid();
        _campLeads.IsLeadAnywhereAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(PrincipalWithRoles(userId), TestContext.Current.CancellationToken);

        result.IsCampLead.Should().BeTrue();
        result.IsTeamCoordinator.Should().BeFalse();
    }

    [HumansFact]
    public async Task Resolve_NotACampLead_IsCampLeadFalse()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(PrincipalWithRoles(Guid.NewGuid()), TestContext.Current.CancellationToken);

        result.IsCampLead.Should().BeFalse();
    }

    [HumansFact]
    public async Task Resolve_AuthWithAdminRole_ReportsSystemRoles()
    {
        var resolver = CreateResolver();
        var user = PrincipalWithRoles(Guid.NewGuid(), RoleNames.Admin, RoleNames.Board);

        var result = await resolver.ResolveAsync(user, TestContext.Current.CancellationToken);

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

        var result = await resolver.ResolveAsync(user, TestContext.Current.CancellationToken);

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

        var result = await resolver.ResolveAsync(user, TestContext.Current.CancellationToken);

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

        var result = await resolver.ResolveAsync(user, TestContext.Current.CancellationToken);

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

        var result = await resolver.ResolveAsync(user, TestContext.Current.CancellationToken);

        result.IsTeamCoordinator.Should().BeTrue();
    }

    [HumansTheory]
    [MemberData(nameof(MappedRoles))]
    public async Task Resolve_EveryRoleAParentheticalCanName_IsProbedAgainstClaims(string role)
    {
        // A role the map can name but the resolver never probes is invisible: the segment
        // carries it among its privileges, but GuideFilter looks for it in SystemRoles,
        // which the resolver never populated.
        var resolver = CreateResolver();
        var user = PrincipalWithRoles(Guid.NewGuid(), role);

        var result = await resolver.ResolveAsync(user, TestContext.Current.CancellationToken);

        result.SystemRoles.Should().Contain(role);
    }

    public static TheoryData<string> MappedRoles()
    {
        var data = new TheoryData<string>();
        foreach (var role in GuideTestRoles.Map.MappedRoles)
        {
            data.Add(role);
        }
        return data;
    }
}
