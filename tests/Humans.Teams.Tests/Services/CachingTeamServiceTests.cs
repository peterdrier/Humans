using Humans.Base.Constants;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Teams.Services;
using Humans.Teams.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NodaTime;
using Xunit;

namespace Humans.Teams.Tests.Services;

public sealed class CachingTeamServiceTests : TeamsTestHarness
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private readonly ServiceProvider _serviceProvider;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ITeamManagementService _innerTeamService;
    private readonly CachingTeamService _service;
    private readonly TeamPageService _pages;

    public CachingTeamServiceTests()
    {
        var userService = NewDbBackedUserService();

        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        _roleAssignmentService
            .IsUserBoardMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _innerTeamService = Substitute.For<ITeamManagementService>();
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => BuildTeamInfosAsync());

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton(userService);
        services.AddSingleton(_roleAssignmentService);
        services.AddKeyedScoped<ITeamManagementService>(
            CachingTeamService.InnerServiceKey,
            (_, _) => _innerTeamService);
        _serviceProvider = services.BuildServiceProvider();

        _service = new CachingTeamService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTeamService>.Instance);
        _pages = new TeamPageService(
            _service,
            Substitute.For<Humans.GoogleIntegration.Contracts.ITeamResourceService>(),
            Substitute.For<Humans.Shifts.Contracts.IShiftManagementService>(),
            Substitute.For<Humans.Settings.Contracts.ISettingsService>(),
            userService,
            _roleAssignmentService);
    }

    [HumansFact]
    public async Task EraseForUserAsync_ErasesThroughInnerAndEvictsTeamGraph()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = MakeTeam(teamId, "Kitchen", parentTeamId: null, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Member)]),
        });
        (await _service.GetUserTeamMembershipsAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Should().HaveCount(1);
        // The inner erase ends the membership in the database.
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = MakeTeam(teamId, "Kitchen", parentTeamId: null, isActive: true),
        });

        await _service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _innerTeamService.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());
        (await _service.GetUserTeamMembershipsAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    [HumansFact]
    public async Task EraseForUserAsync_InnerThrows_StillEvictsTeamGraph()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = MakeTeam(teamId, "Kitchen", parentTeamId: null, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Member)]),
        });
        (await _service.GetUserTeamMembershipsAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Should().HaveCount(1);
        // Memberships ended and committed before a later erase step failed.
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = MakeTeam(teamId, "Kitchen", parentTeamId: null, isActive: true),
        });
        _innerTeamService.EraseForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var act = () => _service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _service.GetUserTeamMembershipsAsync(userId, Xunit.TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetEffectiveBudgetCoordinatorTeamIdsAsync_IncludesDepartmentAndActiveChildren_ForDirectCoordinator()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var activeChildId = Guid.NewGuid();
        var inactiveChildId = Guid.NewGuid();

        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(
                deptId, "Kitchen", parentTeamId: null, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Coordinator)]),
            [activeChildId] = MakeTeam(
                activeChildId, "Prep", parentTeamId: deptId, isActive: true),
            [inactiveChildId] = MakeTeam(
                inactiveChildId, "Retired Sub-team", parentTeamId: deptId, isActive: false),
        };
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo(new[] { deptId, activeChildId });
    }

    [HumansFact]
    public async Task GetEffectiveBudgetCoordinatorTeamIdsAsync_IncludesDepartment_ForManagementRoleHolder()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();

        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(
                deptId, "Logistics", parentTeamId: null, isActive: true,
                managementRoleHolderUserIds: new HashSet<Guid> { userId }),
        };
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo(new[] { deptId });
    }

    [HumansFact]
    public async Task GetEffectiveBudgetCoordinatorTeamIdsAsync_ExcludesChildTeamCoordinatorship_OnlyDepartmentsQualify()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        // User is a coordinator of a CHILD team (which has a parent), not a department.
        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(deptId, "Site Ops", parentTeamId: null, isActive: true),
            [childId] = MakeTeam(
                childId, "Fences", parentTeamId: deptId, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Coordinator)]),
        };
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetEffectiveBudgetCoordinatorTeamIdsAsync_ReturnsEmpty_ForNonCoordinatorMember()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();

        var teams = new Dictionary<Guid, TeamInfo>
        {
            [deptId] = MakeTeam(
                deptId, "Kitchen", parentTeamId: null, isActive: true,
                members: [MakeMember(userId, TeamMemberRole.Member)]),
        };
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await _service.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    private static TeamInfo MakeTeam(
        Guid id,
        string name,
        Guid? parentTeamId,
        bool isActive,
        List<TeamMemberInfo>? members = null,
        IReadOnlySet<Guid>? managementRoleHolderUserIds = null) =>
        new(
            id, name, null, name.ToLowerInvariant().Replace(' ', '-'),
            IsActive: isActive, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: members ?? [],
            ParentTeamId: parentTeamId,
            ManagementRoleHolderUserIds: managementRoleHolderUserIds);

    private static TeamMemberInfo MakeMember(Guid userId, TeamMemberRole role) =>
        new(
            TeamMemberId: Guid.NewGuid(),
            UserId: userId,
            DisplayName: "Member",
            Email: null,
            ProfilePictureUrl: null,
            Role: role,
            JoinedAt: Instant.MinValue);

    [HumansFact]
    public async Task GetTeamDirectoryAsync_ReusesTheCanonicalSnapshotAcrossReads()
    {
        var team = SeedTeam("Public department");
        team.IsPublicPage = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var first = await _pages.GetTeamDirectoryAsync(null, TestContext.Current.CancellationToken);
        var second = await _pages.GetTeamDirectoryAsync(null, TestContext.Current.CancellationToken);

        first.IsAuthenticated.Should().BeFalse();
        first.Departments.Should().ContainSingle().Which.Name.Should().Be(team.Name);
        second.Departments.Should().BeEquivalentTo(first.Departments);
        await _innerTeamService.Received(1).GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_InactiveDirectCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var inactiveTeam = SeedTeam("Inactive", isActive: false);
        SeedTeamMember(inactiveTeam.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(inactiveTeam.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_InactiveParentCoordinator_DoesNotGrantChildAccess()
    {
        var user = SeedUser();
        var inactiveParent = SeedTeam("Inactive Parent", isActive: false);
        var child = SeedTeam("Child");
        child.ParentTeamId = inactiveParent.Id;
        SeedTeamMember(inactiveParent.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesCanonicalUserEmail()
    {
        var user = SeedUser("Alice");
        user.Email = null;
        Db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "alice@example.test",
            IsVerified = true,
            IsPrimary = true,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        });
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        var member = result!.Members.Should().ContainSingle().Subject;
        member.Email.Should().Be("alice@example.test");
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_AfterWarmup_ReturnsAllActiveMemberships()
    {
        var user = SeedUser("Alice");
        var team1 = SeedTeam("Alpha");
        var team2 = SeedTeam("Beta");
        SeedTeamMember(team1.Id, user.Id);
        SeedTeamMember(team2.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Should().Contain(m => m.TeamId == team1.Id && m.Role == TeamMemberRole.Member);
        result.Should().Contain(m => m.TeamId == team2.Id && m.Role == TeamMemberRole.Coordinator);
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_LeftMemberships_ExcludedFromInverseIndex()
    {
        var user = SeedUser("Alice");
        var team = SeedTeam("Alpha");
        var member = SeedTeamMember(team.Id, user.Id);
        member.LeftAt = Clock.GetCurrentInstant();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_SynthesizedTeamHasDisplayNameAndSlug()
    {
        var user = SeedUser("Alice");
        var parent = SeedTeam("Comms");
        var child = SeedTeam("Logo");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(child.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        var membership = result.Should().ContainSingle().Subject;
        membership.TeamSlug.Should().Be("logo");
        membership.TeamName.Should().Be("Logo");
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_AfterMutationInvalidation_ReflectsNewState()
    {
        var user = SeedUser("Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Warm the cache + inverse index.
        var before = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);
        before.Should().HaveCount(1);

        // New membership written outside the decorator: clearing simulates the
        // real path where mutation methods call InvalidateTeamsCache().
        var team2 = SeedTeam("Beta");
        SeedTeamMember(team2.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _service.InvalidateActiveTeamsCache();

        var after = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);
        after.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_ForwardAndInverseIndexAgreeAfterWarmup()
    {
        // Seed several users across overlapping teams; verify the inverse map
        // contains exactly the team IDs derivable from the forward map.
        var alice = SeedUser("Alice");
        var bob = SeedUser("Bob");
        var carol = SeedUser("Carol");
        var t1 = SeedTeam("Alpha");
        var t2 = SeedTeam("Beta");
        var t3 = SeedTeam("Gamma");
        SeedTeamMember(t1.Id, alice.Id);
        SeedTeamMember(t1.Id, bob.Id);
        SeedTeamMember(t2.Id, alice.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(t3.Id, carol.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var aliceTeams = (await _service.GetUserTeamMembershipsAsync(alice.Id, TestContext.Current.CancellationToken))
            .Select(m => m.TeamId).ToHashSet();
        var bobTeams = (await _service.GetUserTeamMembershipsAsync(bob.Id, TestContext.Current.CancellationToken))
            .Select(m => m.TeamId).ToHashSet();
        var carolTeams = (await _service.GetUserTeamMembershipsAsync(carol.Id, TestContext.Current.CancellationToken))
            .Select(m => m.TeamId).ToHashSet();

        aliceTeams.Should().BeEquivalentTo([t1.Id, t2.Id]);
        bobTeams.Should().BeEquivalentTo([t1.Id]);
        carolTeams.Should().BeEquivalentTo([t3.Id]);
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_WarmCache_DoesNotCallInnerService()
    {
        var user = SeedUser("Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // First call drives warmup (which uses repository, not inner service).
        await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        // The inner ITeamManagementService is an unconfigured NSubstitute mock; if the
        // warm-cache path served from the index, no method on it is invoked.
        var inner = _serviceProvider.GetRequiredKeyedService<ITeamManagementService>(
            CachingTeamService.InnerServiceKey);

        var second = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);
        second.Should().HaveCount(1);

        await inner.DidNotReceive().GetUserTeamMembershipsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_UnknownUser_ReturnsEmptyWithoutInnerCall()
    {
        SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
        var inner = _serviceProvider.GetRequiredKeyedService<ITeamManagementService>(
            CachingTeamService.InnerServiceKey);
        await inner.DidNotReceive().GetUserTeamMembershipsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // GetMyTeamMembershipsAsync â€” cache-served (issue nobodies-collective/Humans#748)
    // ==========================================================================

    [HumansTheory]
    [InlineData(null)]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task MyTeams_HiddenMembershipNamesAndCounts_RequirePrivilegedRole(string? role)
    {
        var user = SeedUser("Coordinator");
        _roleAssignmentService.IsUserAdminAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(string.Equals(role, RoleNames.Admin, StringComparison.Ordinal));
        _roleAssignmentService.IsUserBoardMemberAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(string.Equals(role, RoleNames.Board, StringComparison.Ordinal));
        _roleAssignmentService.IsUserTeamsAdminAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(string.Equals(role, RoleNames.TeamsAdmin, StringComparison.Ordinal));
        var hiddenParent = SeedTeam("Private parent");
        hiddenParent.IsHidden = true;
        var publicChild = SeedTeam("Visible child");
        publicChild.ParentTeamId = hiddenParent.Id;
        var publicParent = SeedTeam("Visible parent");
        var hiddenChild = SeedTeam("Private child");
        hiddenChild.ParentTeamId = publicParent.Id;
        hiddenChild.IsHidden = true;
        foreach (var team in new[] { hiddenParent, publicChild, publicParent, hiddenChild })
            SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(publicParent.Id, SeedUser("Public requester").Id);
        SeedJoinRequest(hiddenChild.Id, SeedUser("Private requester").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        var privileged = role is not null;
        result.Select(row => row.TeamId).Should().BeEquivalentTo(privileged
            ? new[] { hiddenParent.Id, publicChild.Id, publicParent.Id, hiddenChild.Id }
            : new[] { publicChild.Id, publicParent.Id });
        result.Single(row => row.TeamId == publicChild.Id).TeamName.Should().Be(privileged
            ? "Private parent - Visible child" : "Visible child");
        result.Single(row => row.TeamId == publicParent.Id).PendingRequestCount.Should().Be(privileged ? 2 : 1);
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_Coordinator_GetsPendingCountsForManageableNonSystemTeams()
    {
        var user = SeedUser("Coordinator");
        var managedTeam = SeedTeam("Alpha");
        var systemTeam = SeedTeam("Volunteers");
        systemTeam.SystemTeamType = SystemTeamType.Volunteers;
        SeedTeamMember(managedTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(systemTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(managedTeam.Id, SeedUser("Requester A").Id);
        SeedJoinRequest(systemTeam.Id, SeedUser("Requester B").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Single(m => m.TeamId == managedTeam.Id).PendingRequestCount.Should().Be(1);
        result.Single(m => m.TeamId == managedTeam.Id).CanLeave.Should().BeTrue();
        result.Single(m => m.TeamId == systemTeam.Id).PendingRequestCount.Should().Be(0);
        result.Single(m => m.TeamId == systemTeam.Id).CanLeave.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_BoardMember_GetsPendingCountsForRegularMemberships()
    {
        var user = SeedUser("Board Human");
        _roleAssignmentService
            .IsUserBoardMemberAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(true);
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedJoinRequest(team.Id, SeedUser("Requester").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
        result[0].Role.Should().Be(TeamMemberRole.Member);
        result[0].PendingRequestCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_Coordinator_AggregatesChildTeamPendingCounts()
    {
        var user = SeedUser("Department Coordinator");
        var department = SeedTeam("Department");
        var child = SeedTeam("Child");
        child.ParentTeamId = department.Id;
        SeedTeamMember(department.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(department.Id, SeedUser("Direct Requester").Id);
        SeedJoinRequest(child.Id, SeedUser("Child Requester A").Id);
        SeedJoinRequest(child.Id, SeedUser("Child Requester B").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        var deptRow = result.Should().ContainSingle(m => m.TeamId == department.Id).Subject;
        deptRow.PendingRequestCount.Should().Be(3);
        deptRow.TeamName.Should().Be("Department");
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_InheritedCoordinator_GetsPendingCountForChildMembership()
    {
        // User coordinates the parent and is a regular Member on the child.
        // Per CanUserApproveRequestsForTeamAsync's parent-walk semantics, they
        // can manage the child team, so the child membership row must surface
        // its pending count.
        var user = SeedUser("Inherited Coordinator");
        var parent = SeedTeam("Parent");
        var child = SeedTeam("Child");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(child.Id, user.Id);
        SeedJoinRequest(child.Id, SeedUser("Child Requester").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        var childRow = result.Should().ContainSingle(m => m.TeamId == child.Id).Subject;
        childRow.Role.Should().Be(TeamMemberRole.Member);
        childRow.PendingRequestCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_NonCoordinatorNonBoard_DoesNotCountPending()
    {
        var user = SeedUser("Regular Member");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedJoinRequest(team.Id, SeedUser("Requester").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
        result[0].PendingRequestCount.Should().Be(0);
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_ChildTeamMembership_DisplayNameIsParentDashChild()
    {
        var user = SeedUser("Alice");
        var parent = SeedTeam("Comms");
        var child = SeedTeam("Logo");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(child.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        var row = result.Should().ContainSingle().Subject;
        row.TeamName.Should().Be("Comms - Logo");
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_WarmCache_DoesNotCallInnerService()
    {
        var user = SeedUser("Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(team.Id, SeedUser("Requester").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // The page assembler warms the canonical snapshot through the decorator.
        var first = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);
        first.Should().ContainSingle();
        first[0].PendingRequestCount.Should().Be(1);

        var inner = _serviceProvider.GetRequiredKeyedService<ITeamManagementService>(
            CachingTeamService.InnerServiceKey);

        // A second page read must reuse the warmed snapshot.
        var second = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);
        second.Should().ContainSingle();

        await inner.Received(1).GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Join-lifecycle invalidation â€” issue nobodies-collective/Humans#748
    // ==========================================================================

    [HumansTheory]
    [InlineData(nameof(CachingTeamService.JoinTeamAsync))]
    [InlineData(nameof(CachingTeamService.WithdrawJoinRequestAsync))]
    [InlineData(nameof(CachingTeamService.RejectJoinRequestAsync))]
    [InlineData(nameof(CachingTeamService.ApproveJoinRequestAsync))]
    public async Task JoinLifecycleWrite_InvalidatesCache(string write)
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        // Warm the cache so we can observe invalidation by counter.
        await _service.GetTeamAsync(team.Id, ct);
        var before = _service.BulkInvalidations;

        // Inner is an unconfigured NSubstitute mock; approve returns default (null) — fine for this assertion.
        await (write switch
        {
            nameof(CachingTeamService.JoinTeamAsync) => _service.JoinTeamAsync(team.Id, Guid.NewGuid(), null, ct),
            nameof(CachingTeamService.WithdrawJoinRequestAsync) => _service.WithdrawJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid(), ct),
            nameof(CachingTeamService.RejectJoinRequestAsync) => _service.RejectJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid(), "reason", ct),
            _ => _service.ApproveJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid(), null, ct),
        });

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task UpdateTeamPageContentAsync_FailedResult_InvalidatesCache()
    {
        // The inner service reports a post-commit dependency failure as a failed
        // result, so a failure cannot prove the cached team is unchanged.
        var ct = TestContext.Current.CancellationToken;
        var team = SeedTeam("Alpha");
        await SaveAllAsync(ct);
        await _service.GetTeamAsync(team.Id, ct);
        var before = _service.BulkInvalidations;
        _innerTeamService.UpdateTeamPageContentAsync(
                team.Id, null, Arg.Any<IReadOnlyList<TeamPageCallToActionInput>>(), false, false, Arg.Any<Guid>(), ct)
            .Returns(new TeamPageUpdateResult(false, null));

        await _service.UpdateTeamPageContentAsync(team.Id, null, [], false, false, Guid.NewGuid(), ct);

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task FailedMembershipWrite_RebuildsTeamAndUserIndexes(bool joining, bool cancelled)
    {
        var ct = TestContext.Current.CancellationToken;
        var team = SeedTeam("Alpha");
        var user = SeedUser("Coordinator");
        var member = joining ? null : SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(ct);
        await _service.GetUserTeamMembershipsAsync(user.Id, ct);
        (await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, ct)).Should().BeFalse();

        Exception failure = cancelled
            ? new OperationCanceledException(ct)
            : new InvalidOperationException("Post-write dependency failed");
        async Task PersistThenFail()
        {
            if (member is null)
                SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
            else
                member.Role = TeamMemberRole.Coordinator;
            await SaveAllAsync(ct);
            throw failure;
        }

        Func<Task> write;
        if (joining)
        {
            _innerTeamService.JoinTeamAsync(team.Id, user.Id, null, ct)
                .Returns(async _ => { await PersistThenFail(); return default!; });
            write = () => _service.JoinTeamAsync(team.Id, user.Id, null, ct);
        }
        else
        {
            _innerTeamService.SetMemberRoleAsync(team.Id, user.Id, TeamMemberRole.Coordinator, user.Id, ct)
                .Returns(_ => PersistThenFail());
            write = () => _service.SetMemberRoleAsync(team.Id, user.Id, TeamMemberRole.Coordinator, user.Id, ct);
        }

        (await write.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        (await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, ct)).Should().BeTrue();
        var memberships = await _service.GetUserTeamMembershipsAsync(user.Id, ct);
        memberships.Should().ContainSingle().Which.Role.Should().Be(TeamMemberRole.Coordinator);
    }

    [HumansFact]
    public async Task FailedRevocation_RemovesCachedCoordinatorAccessAndMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = SeedTeam("Alpha");
        var user = SeedUser("Former coordinator");
        var member = SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(ct);
        (await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, ct)).Should().BeTrue();
        (await _service.GetUserTeamMembershipsAsync(user.Id, ct)).Should().ContainSingle();

        var failure = new InvalidOperationException("Post-write dependency failed");
        _innerTeamService.RevokeAllMembershipsAsync(user.Id, ct).Returns(async _ =>
        {
            TeamsDb.TeamMembers.Remove(member);
            await SaveAllAsync(ct);
            return await Task.FromException<int>(failure);
        });

        Func<Task> revoke = () => _service.RevokeAllMembershipsAsync(user.Id, ct);
        (await revoke.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        (await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, ct)).Should().BeFalse();
        (await _service.GetUserTeamMembershipsAsync(user.Id, ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task WarmAllAsync_PopulatesTeamInfoPendingRequestCount()
    {
        var team = SeedTeam("Alpha");
        SeedJoinRequest(team.Id, SeedUser("Requester A").Id);
        SeedJoinRequest(team.Id, SeedUser("Requester B").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var info = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        info.Should().NotBeNull();
        info.PendingRequestCount.Should().Be(2);
    }

    [HumansFact]
    public async Task SearchAsync_ServesFromCache_MatchesByName_ExcludesHidden()
    {
        SeedTeam("Kitchen");
        var hidden = SeedTeam("Kitchenette");
        hidden.IsHidden = true;
        SeedTeam("Gate");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var results = await _service.SearchAsync("kitchen", int.MaxValue, TestContext.Current.CancellationToken);

        // Cache-served name match: case-insensitive, hidden teams excluded, non-matches dropped.
        results.Select(r => r.Name).Should().ContainSingle().Which.Should().Be("Kitchen");

        // Search must never reach the inner (DB-backed) service.
        await _innerTeamService.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ResolvesAHiddenTeam_BecauseTheDestinationPageEnforces()
    {
        // Ruling on nobodies-collective/Humans#985 (2026-08-07): a by-GUID hit is a routing
        // convenience for someone who already holds the id, not an authorization statement.
        // The pairing test below — GetTeamDetailAsync_HiddenTeam_NonPrivilegedViewer — is what
        // makes that safe: /Teams/{slug} is where the hidden team is actually refused.
        var hidden = SeedTeam("Kitchenette");
        hidden.IsHidden = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var results = await _service.SearchAsync(
            hidden.Id.ToString(), int.MaxValue, TestContext.Current.CancellationToken);

        var hit = results.Should().ContainSingle().Subject;
        hit.TeamId.Should().Be(hidden.Id);
        hit.Name.Should().Be("Kitchenette");
        hit.Score.Should().Be(StringSearchExtensions.ExactNameScore,
            because: "an id paste is as exact as a match gets");
    }

    [HumansFact]
    public async Task SearchAsync_ScoresItsOwnHits_ExactThenPrefixThenContains()
    {
        // Scoring belongs to the section, not the global-search orchestrator
        // (nobodies-collective/Humans#1062).
        SeedTeam("Kitchen");
        SeedTeam("Kitchen Crew");
        SeedTeam("Main Kitchen");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var results = await _service.SearchAsync("Kitchen", int.MaxValue, TestContext.Current.CancellationToken);

        results.Should().BeEquivalentTo(new[]
        {
            new { Name = "Kitchen", Score = StringSearchExtensions.ExactNameScore },
            new { Name = "Kitchen Crew", Score = StringSearchExtensions.PrefixNameScore },
            new { Name = "Main Kitchen", Score = StringSearchExtensions.ContainsNameScore },
        });
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_HiddenTeam_NonPrivilegedViewer_ReturnsNull()
    {
        // Destination-page enforcement for the ruling above. TeamController.Details turns this
        // null into NotFound(), so pasting a hidden team's GUID into /Search buys the caller a
        // link and a 404 — never the page.
        var viewer = SeedUser("Plain Human");
        var hidden = SeedTeam("Kitchenette");
        hidden.IsHidden = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var detail = await _pages.GetTeamDetailAsync(
            hidden.Slug, viewer.Id, TestContext.Current.CancellationToken);

        detail.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_HiddenTeam_AnonymousViewer_ReturnsNull()
    {
        var hidden = SeedTeam("Kitchenette");
        hidden.IsHidden = true;
        hidden.IsPublicPage = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var detail = await _pages.GetTeamDetailAsync(
            hidden.Slug, userId: null, TestContext.Current.CancellationToken);

        detail.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_HiddenTeam_TeamsAdmin_ReturnsTheDetail()
    {
        var admin = SeedUser("Teams Admin");
        var hidden = SeedTeam("Kitchenette");
        hidden.IsHidden = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _roleAssignmentService.IsUserTeamsAdminAsync(admin.Id, Arg.Any<CancellationToken>())
            .Returns(true);
        _innerTeamService.GetPendingRequestsForTeamAsync(hidden.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TeamJoinRequestSnapshot>>([]));

        var detail = await _pages.GetTeamDetailAsync(
            hidden.Slug, admin.Id, TestContext.Current.CancellationToken);

        detail.Should().NotBeNull();
        detail.Team.Name.Should().Be("Kitchenette");
    }

    private void SeedJoinRequest(Guid teamId, Guid userId)
    {
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = Clock.GetCurrentInstant()
        };
        TeamsDb.TeamJoinRequests.Add(request);
    }

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> BuildTeamInfosAsync()
    {
        var teams = await TeamsDb.Teams.AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync(TestContext.Current.CancellationToken);
        var userIds = teams
            .SelectMany(t => t.Members.Select(m => m.UserId))
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, User>()
            : await Db.Users.AsNoTracking()
                .Include(u => u.UserEmails)
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, TestContext.Current.CancellationToken);
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var pendingRows = await TeamsDb.TeamJoinRequests.AsNoTracking()
            .Where(r => r.Status == TeamJoinRequestStatus.Pending)
            .ToListAsync(TestContext.Current.CancellationToken);
        var pendingCounts = pendingRows
            .GroupBy(r => r.TeamId)
            .ToDictionary(g => g.Key, g => g.Count());

        return teams.ToDictionary(
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
                    .Select(m =>
                    {
                        users.TryGetValue(m.UserId, out var user);
                        var email = user?.UserEmails.FirstOrDefault(e => e.IsPrimary)?.Email ?? user?.Email;
                        return new TeamMemberInfo(
                            m.Id,
                            m.UserId,
                            user is null ? string.Empty : LegacyDisplayName(user),
                            email,
                            user?.ProfilePictureUrl,
                            m.Role,
                            m.JoinedAt);
                    })
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
                PageContentUpdatedByUserId: t.PageContentUpdatedByUserId,
                PendingRequestCount: pendingCounts.TryGetValue(t.Id, out var pending) ? pending : 0));
    }

    private static string LegacyDisplayName(User user) =>
        (string?)LegacyDisplayNameProperty.GetValue(user) ?? string.Empty;

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }
}
