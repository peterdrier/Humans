using Humans.Testing;
using Microsoft.Extensions.Localization;
using Humans.Base.Extensions;
using Humans.Base.Constants;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// Pins that <see cref="TeamPageService.GetTeamDetailAsync"/>
/// projects entirely from the cached <c>TeamInfo</c> snapshot.
/// </summary>
public sealed class CachingTeamServiceGetTeamDetailTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ITeamManagementService _innerTeamService;
    private readonly ISystemRoleService _systemRoleService;
    private readonly TeamPageService _pages;

    public CachingTeamServiceGetTeamDetailTests()
    {
        _innerTeamService = Substitute.For<ITeamManagementService>();
        _innerTeamService.GetTeamSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var teams = await _innerTeamService.GetTeamsAsync(call.Arg<CancellationToken>());
                return (IReadOnlyDictionary<Guid, TeamReadSnapshot>)teams.ToDictionary(pair => pair.Key, pair => new TeamReadSnapshot(
                    pair.Value, pair.Value.Members.Select(member => new TeamMemberReference(
                        member.TeamMemberId, member.UserId, member.Role, member.JoinedAt)).ToList()));
            });
        _innerTeamService.ResolveTeamMembersAsync(
                Arg.Any<IReadOnlyDictionary<Guid, TeamReadSnapshot>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                call.Arg<IReadOnlyDictionary<Guid, TeamReadSnapshot>>()
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Team)));
        _systemRoleService = Substitute.For<ISystemRoleService>();
        var userService = Substitute.For<IUserService>();

        userService
            .GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton(userService);
        services.AddSingleton(_systemRoleService);
        services.AddKeyedScoped<ITeamManagementService>(
            CachingTeamService.InnerServiceKey,
            (_, _) => _innerTeamService);
        _serviceProvider = services.BuildServiceProvider();

        var service = new CachingTeamService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTeamService>.Instance, Substitute.For<IStringLocalizer<TeamsResource>>().AddFormatting());
        _pages = new TeamPageService(
            service,
            Substitute.For<Humans.Google.Contracts.ITeamResourceService>(),
            _systemRoleService);
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task GetTeamDetailAsync_NonmemberCanJoinOnlyAnActiveTeam(bool isActive)
    {
        var team = MakeTeam("Alpha");
        team.IsActive = isActive;
        SeedTeams(team);

        var result = await _pages.GetTeamDetailAsync(team.Slug, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.CanCurrentUserJoin.Should().Be(isActive);
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousVisible_DoesNotCallBypassedRepositoryReads()
    {
        var team = MakeTeam("Public", isPublicPage: true);
        SeedTeams(team);

        var result = await _pages.GetTeamDetailAsync("public", userId: null, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Team.Slug.Should().Be("public");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GetTeamDetailAsync_OmitsUnsafeStoredActionsWithoutMutatingSource(bool authenticated)
    {
        var team = MakeTeam("Public", isPublicPage: true);
        team.CallsToAction =
        [
            new CallToAction { Text = "Unsafe", Url = "javascript:alert(1)" },
            new CallToAction { Text = "Obfuscated", Url = "java\tscript:alert(1)" },
            new CallToAction { Text = "Join", Url = "/join" }
        ];
        SeedTeams(team);

        var result = await _pages.GetTeamDetailAsync("public", authenticated ? Guid.NewGuid() : null,
            Xunit.TestContext.Current.CancellationToken);

        result!.Team.CallsToAction.Should().ContainSingle().Which.Url.Should().Be("/join");
        team.CallsToAction.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousHiddenTeam_ReturnsNullWithoutRepositoryReads()
    {
        var team = MakeTeam("Hidden", isPublicPage: true, isHidden: true);
        SeedTeams(team);

        var result = await _pages.GetTeamDetailAsync("hidden", userId: null, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AuthenticatedMember_ProjectsFromCacheWithoutBypassedReads()
    {
        var team = MakeTeam("Alpha", isPublicPage: true);
        var memberId = Guid.NewGuid();
        team.Members.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberId,
            Role = TeamMemberRole.Member,
            JoinedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        });
        SeedTeams(team);

        _innerTeamService
            .GetUserPendingRequestAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamJoinRequestSnapshot?)null);
        _systemRoleService.IsUserBoardMemberAsync(memberId, Arg.Any<CancellationToken>()).Returns(false);
        _systemRoleService.IsUserAdminAsync(memberId, Arg.Any<CancellationToken>()).Returns(false);
        _systemRoleService.IsUserTeamsAdminAsync(memberId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _pages.GetTeamDetailAsync("alpha", memberId, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.IsAuthenticated.Should().BeTrue();
        result.IsCurrentUserMember.Should().BeTrue();
        result.CanCurrentUserLeave.Should().BeTrue();
        result.CanCurrentUserJoin.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_ResolvesViaCustomSlug()
    {
        var team = MakeTeam("Original", isPublicPage: true);
        team.CustomSlug = "branded";
        SeedTeams(team);

        var result = await _pages.GetTeamDetailAsync("Branded", userId: null, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Team.Slug.Should().Be("original");
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AuthenticatedManager_WalksChildTeamIdsFromCache()
    {
        var parent = MakeTeam("Department", isPublicPage: true);
        var child = MakeTeam("Subteam");
        child.ParentTeamId = parent.Id;
        SeedTeams(parent, child);

        var viewerId = Guid.NewGuid();
        _innerTeamService
            .GetUserPendingRequestAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamJoinRequestSnapshot?)null);
        _systemRoleService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>()).Returns(false);
        _systemRoleService.IsUserAdminAsync(viewerId, Arg.Any<CancellationToken>()).Returns(false);
        _systemRoleService.IsUserTeamsAdminAsync(viewerId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _pages.GetTeamDetailAsync("department", viewerId, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.ChildTeams.Should().ContainSingle(c => c.Id == child.Id);
    }

    [HumansTheory]
    [Xunit.InlineData("anonymous")]
    [Xunit.InlineData("member")]
    [Xunit.InlineData(RoleNames.Admin)]
    [Xunit.InlineData(RoleNames.Board)]
    [Xunit.InlineData(TeamsRoles.TeamsAdmin)]
    public async Task Detail_HiddenParentAndChildLinks_RequirePrivilegedRole(string role)
    {
        var root = MakeTeam("VisibleRoot", isPublicPage: true);
        var hiddenChild = MakeTeam("PrivateChild", isHidden: true);
        hiddenChild.ParentTeamId = root.Id;
        var hiddenParent = MakeTeam("PrivateParent", isHidden: true);
        var child = MakeTeam("VisibleChild");
        child.ParentTeamId = hiddenParent.Id;
        child.IsPromotedToDirectory = true;
        SeedTeams(root, hiddenChild, hiddenParent, child);
        var viewer = Guid.NewGuid();
        _systemRoleService.IsUserAdminAsync(viewer, Arg.Any<CancellationToken>()).Returns(string.Equals(role, RoleNames.Admin, StringComparison.Ordinal));
        _systemRoleService.IsUserBoardMemberAsync(viewer, Arg.Any<CancellationToken>()).Returns(string.Equals(role, RoleNames.Board, StringComparison.Ordinal));
        _systemRoleService.IsUserTeamsAdminAsync(viewer, Arg.Any<CancellationToken>()).Returns(string.Equals(role, TeamsRoles.TeamsAdmin, StringComparison.Ordinal));
        Guid? userId = string.Equals(role, "anonymous", StringComparison.Ordinal) ? null : viewer;

        var rootPage = await _pages.GetTeamDetailAsync(root.Slug, userId, Xunit.TestContext.Current.CancellationToken);
        var childPage = await _pages.GetTeamDetailAsync(child.Slug, userId, Xunit.TestContext.Current.CancellationToken);

        rootPage.Should().NotBeNull();
        childPage.Should().NotBeNull();
        var privileged = role is RoleNames.Admin or RoleNames.Board or TeamsRoles.TeamsAdmin;
        rootPage.ChildTeams.Select(team => team.Id).Should().Equal(privileged ? new[] { hiddenChild.Id } : []);
        var parentId = childPage.Team.ParentTeam?.Id;
        parentId.Should().Be(privileged ? hiddenParent.Id : null);
        childPage.Team.DisplayName.Should().Be(privileged ? "PrivateParent - VisibleChild" : "VisibleChild");
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousChildVisibility_FiltersHiddenAndPrivateChildren()
    {
        var parent = MakeTeam("Dept", isPublicPage: true);
        var publicChild = MakeTeam("PublicChild", isPublicPage: true);
        publicChild.ParentTeamId = parent.Id;
        var hiddenChild = MakeTeam("HiddenChild", isPublicPage: true, isHidden: true);
        hiddenChild.ParentTeamId = parent.Id;
        var privateChild = MakeTeam("PrivateChild", isPublicPage: false);
        privateChild.ParentTeamId = parent.Id;
        SeedTeams(parent, publicChild, hiddenChild, privateChild);

        var result = await _pages.GetTeamDetailAsync("dept", userId: null, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.ChildTeams.Select(c => c.Id).Should().BeEquivalentTo([publicChild.Id]);
    }

    [HumansFact]
    public async Task TeamPage_MemberInMultipleSubteams_KeepsFirstNameOrderedAffiliation()
    {
        var parent = MakeTeam("Department");
        var zeta = MakeTeam("Zeta");
        var alpha = MakeTeam("Alpha");
        zeta.ParentTeamId = parent.Id;
        alpha.ParentTeamId = parent.Id;
        var memberId = Guid.NewGuid();
        foreach (var child in new[] { zeta, alpha })
            child.Members.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = child.Id,
                UserId = memberId,
                Role = child == zeta ? TeamMemberRole.Coordinator : TeamMemberRole.Member,
                JoinedAt = child.CreatedAt
            });
        SeedTeams(parent, zeta, alpha);

        var page = await _pages.GetTeamPageDetailAsync("department", Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        var member = page!.SubteamMembers.Should().ContainSingle().Subject;
        member.UserId.Should().Be(memberId);
        member.ChildTeamName.Should().Be("Alpha");
        member.IsCoordinator.Should().BeFalse();
        page.SubteamLeads.Should().ContainSingle().Which.ChildTeamName.Should().Be("Zeta");
    }

    [HumansFact]
    public async Task TeamPage_SubteamRoleTitles_UsePublicManagementDefinitionsFromTheMemberSnapshot()
    {
        using var culture = new CultureScope("de");
        var ct = Xunit.TestContext.Current.CancellationToken;
        var parent = MakeTeam("Department");
        var child = MakeTeam("Child");
        var privateRoleChild = MakeTeam("Private role");
        child.ParentTeamId = privateRoleChild.ParentTeamId = parent.Id;
        var coordinator = Guid.NewGuid();
        var member = Guid.NewGuid();
        foreach (var (team, userId, role) in new[]
        {
            (child, coordinator, TeamMemberRole.Coordinator),
            (child, member, TeamMemberRole.Member),
            (privateRoleChild, Guid.NewGuid(), TeamMemberRole.Coordinator),
        })
            team.Members.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                UserId = userId,
                Role = role,
                JoinedAt = team.CreatedAt,
            });
        SeedTeams(parent, child, privateRoleChild);
        var teams = (await _innerTeamService.GetTeamsAsync(ct)).ToDictionary(pair => pair.Key, pair => pair.Value);
        TeamRoleDefinitionSnapshot Definition(string name, int sortOrder, bool management = true, bool visible = true) =>
            new(Guid.NewGuid(), child.Id, child.Name, child.Slug, name, null, 1, null, [], sortOrder,
                management, RolePeriod.YearRound, visible, []);
        teams[child.Id] = teams[child.Id] with
        {
            RoleDefinitions = [Definition("Private", -2, visible: false), Definition("Ordinary", -1, management: false),
                Definition("Earlier in input", 2), Definition("Zulu", 1), Definition("Ärea", 1)],
        };
        teams[privateRoleChild.Id] = teams[privateRoleChild.Id] with
        {
            RoleDefinitions = [Definition("Private", 0, visible: false) with
                { TeamId = privateRoleChild.Id, TeamName = privateRoleChild.Name, TeamSlug = privateRoleChild.Slug }],
        };
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var page = await _pages.GetTeamPageDetailAsync(parent.Slug, Guid.NewGuid(), ct);

        page.Should().NotBeNull();
        page.SubteamLeads.Single(lead => string.Equals(lead.ChildTeamSlug, child.Slug, StringComparison.Ordinal)).RoleTitle.Should().Be("Ärea");
        page.SubteamLeads.Single(lead => string.Equals(lead.ChildTeamSlug, privateRoleChild.Slug, StringComparison.Ordinal)).RoleTitle.Should().BeNull();
        page.SubteamMembers.Single(person => person.UserId == coordinator).RoleTitle.Should().Be("Ärea");
        page.SubteamMembers.Single(person => person.UserId == member).RoleTitle.Should().BeNull();
    }

    private void SeedTeams(params Team[] teams)
    {
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var infos = teams.ToDictionary(
            t => t.Id,
            t => new TeamInfo(
                t.Id,
                t.Name,
                t.Description,
                t.Slug,
                t.IsActive,
                t.IsSystemTeam,
                t.SystemTeamType,
                t.RequiresApproval,
                t.IsPublicPage,
                t.IsHidden,
                t.IsPromotedToDirectory,
                t.CreatedAt,
                t.Members
                    .Where(m => m.LeftAt is null)
                    .Select(m => new TeamMemberInfo(
                        m.Id,
                        m.UserId,
                        string.Empty,
                        null,
                        null,
                        m.Role,
                        m.JoinedAt))
                    .ToList(),
                ParentTeamId: t.ParentTeamId,
                GoogleGroupPrefix: t.GoogleGroupPrefix,
                HasBudget: t.HasBudget,
                IsSensitive: t.IsSensitive,
                UpdatedAt: t.UpdatedAt,
                CustomSlug: t.CustomSlug,
                ChildTeamIds: childIdsByParent.TryGetValue(t.Id, out var childIds) ? childIds : null,
                ShowCoordinatorsOnPublicPage: t.ShowCoordinatorsOnPublicPage,
                PageContent: t.PageContent,
                CallsToAction: t.CallsToAction,
                PageContentUpdatedAt: t.PageContentUpdatedAt,
                PageContentUpdatedByUserId: t.PageContentUpdatedByUserId));
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(infos);
    }

    private static Team MakeTeam(string name, bool isPublicPage = false, bool isHidden = false)
    {
        return new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            IsPublicPage = isPublicPage,
            IsHidden = isHidden,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        };
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
