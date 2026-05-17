using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Teams;
using Humans.Infrastructure.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class CachingTeamServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly ServiceProvider _serviceProvider;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly CachingTeamService _service;

    public CachingTeamServiceTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(_options);

        var userService = Substitute.For<IUserService>();
        userService
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                return Task.FromResult(LoadUsers(ids));
            });
        userService.StubGetUserInfosFromDb(_options);

        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        _roleAssignmentService
            .IsUserBoardMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton(_roleAssignmentService);
        services.AddKeyedScoped<ITeamService>(
            CachingTeamService.InnerServiceKey,
            (_, _) => Substitute.For<ITeamService>());
        _serviceProvider = services.BuildServiceProvider();

        ITeamRepository teamRepository = new TeamRepository(new TestDbContextFactory(_options));
        _service = new CachingTeamService(
            teamRepository,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTeamService>.Instance);
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_InactiveDirectCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var inactiveTeam = SeedTeam("Inactive", isActive: false);
        SeedTeamMember(inactiveTeam.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(inactiveTeam.Id, user.Id);

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
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesCanonicalUserEmail()
    {
        var user = SeedUser("Alice");
        user.Email = null;
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "alice@example.test",
            IsVerified = true,
            IsPrimary = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        var member = result!.Members.Should().ContainSingle().Subject;
        member.Email.Should().Be("alice@example.test");
    }

    private IReadOnlyDictionary<Guid, User> LoadUsers(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, User>();

        using var db = new HumansDbContext(_options);
        return db.Users.AsNoTracking()
            .Include(u => u.UserEmails)
            .Where(u => ids.Contains(u.Id))
            .ToDictionary(u => u.Id);
    }

    private User SeedUser(string displayName = "Test User")
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private Team SeedTeam(string name, bool isActive = true)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            IsActive = isActive,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private TeamMember SeedTeamMember(Guid teamId, Guid userId, TeamMemberRole role)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamMembers.Add(member);
        return member;
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_AfterWarmup_ReturnsAllActiveMemberships()
    {
        var user = SeedUser("Alice");
        var team1 = SeedTeam("Alpha");
        var team2 = SeedTeam("Beta");
        SeedTeamMember(team1.Id, user.Id, TeamMemberRole.Member);
        SeedTeamMember(team2.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Should().HaveCount(2);
        result.Should().Contain(m => m.TeamId == team1.Id && m.Role == TeamMemberRole.Member);
        result.Should().Contain(m => m.TeamId == team2.Id && m.Role == TeamMemberRole.Coordinator);
        result.Should().OnlyContain(m => m.LeftAt == null);
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_LeftMemberships_ExcludedFromInverseIndex()
    {
        var user = SeedUser("Alice");
        var team = SeedTeam("Alpha");
        var member = SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        member.LeftAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_SynthesizedTeamHasDisplayNameAndSlug()
    {
        var user = SeedUser("Alice");
        var parent = SeedTeam("Comms");
        var child = SeedTeam("Logo");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(child.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        var membership = result.Should().ContainSingle().Subject;
        membership.Team.Should().NotBeNull();
        membership.Team.Slug.Should().Be("logo");
        // Team.DisplayName concatenates ParentTeam.Name when ParentTeam is set;
        // synthesized entity must populate ParentTeam so consumers like
        // ProfileCardViewComponent render "Parent - Child" without an EF round-trip.
        membership.Team.DisplayName.Should().Be("Comms - Logo");
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_AfterMutationInvalidation_ReflectsNewState()
    {
        var user = SeedUser("Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        // Warm the cache + inverse index.
        var before = await _service.GetUserTeamsAsync(user.Id);
        before.Should().HaveCount(1);

        // New membership written outside the decorator: clearing simulates the
        // real path where mutation methods call InvalidateTeamsCache().
        var team2 = SeedTeam("Beta");
        SeedTeamMember(team2.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();
        _service.InvalidateActiveTeamsCache();

        var after = await _service.GetUserTeamsAsync(user.Id);
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
        SeedTeamMember(t1.Id, alice.Id, TeamMemberRole.Member);
        SeedTeamMember(t1.Id, bob.Id, TeamMemberRole.Member);
        SeedTeamMember(t2.Id, alice.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(t3.Id, carol.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var aliceTeams = (await _service.GetUserTeamsAsync(alice.Id))
            .Select(m => m.TeamId).ToHashSet();
        var bobTeams = (await _service.GetUserTeamsAsync(bob.Id))
            .Select(m => m.TeamId).ToHashSet();
        var carolTeams = (await _service.GetUserTeamsAsync(carol.Id))
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
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        // First call drives warmup (which uses repository, not inner service).
        await _service.GetUserTeamsAsync(user.Id);

        // The inner ITeamService is an unconfigured NSubstitute mock; if the
        // warm-cache path served from the index, no method on it is invoked.
        var inner = _serviceProvider.GetRequiredKeyedService<ITeamService>(
            CachingTeamService.InnerServiceKey);

        var second = await _service.GetUserTeamsAsync(user.Id);
        second.Should().HaveCount(1);

        await inner.DidNotReceive().GetUserTeamsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_UnknownUser_ReturnsEmptyWithoutInnerCall()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(Guid.NewGuid());

        result.Should().BeEmpty();
        var inner = _serviceProvider.GetRequiredKeyedService<ITeamService>(
            CachingTeamService.InnerServiceKey);
        await inner.DidNotReceive().GetUserTeamsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // GetMyTeamMembershipsAsync — cache-served (issue nobodies-collective/Humans#748)
    // ==========================================================================

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
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

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
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        SeedJoinRequest(team.Id, SeedUser("Requester").Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

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
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

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
        SeedTeamMember(child.Id, user.Id, TeamMemberRole.Member);
        SeedJoinRequest(child.Id, SeedUser("Child Requester").Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

        var childRow = result.Should().ContainSingle(m => m.TeamId == child.Id).Subject;
        childRow.Role.Should().Be(TeamMemberRole.Member);
        childRow.PendingRequestCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_NonCoordinatorNonBoard_DoesNotCountPending()
    {
        var user = SeedUser("Regular Member");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        SeedJoinRequest(team.Id, SeedUser("Requester").Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

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
        SeedTeamMember(child.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

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
        await _dbContext.SaveChangesAsync();

        // Drive a first call to warm the cache (which uses the repository, not
        // the inner service).
        var first = await _service.GetMyTeamMembershipsAsync(user.Id);
        first.Should().ContainSingle();
        first[0].PendingRequestCount.Should().Be(1);

        var inner = _serviceProvider.GetRequiredKeyedService<ITeamService>(
            CachingTeamService.InnerServiceKey);

        // A warm-cache second call must NOT touch the inner ITeamService — the
        // T-01 zero-EF-on-warm assertion for GetMyTeamMembershipsAsync.
        var second = await _service.GetMyTeamMembershipsAsync(user.Id);
        second.Should().ContainSingle();

        await inner.DidNotReceive()
            .GetMyTeamMembershipsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Join-lifecycle invalidation — issue nobodies-collective/Humans#748
    // ==========================================================================

    [HumansFact]
    public async Task RequestToJoinTeamAsync_InvalidatesCache()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        // Warm the cache so we can observe invalidation by counter.
        await _service.GetTeamAsync(team.Id);
        var before = _service.BulkInvalidations;

        await _service.RequestToJoinTeamAsync(team.Id, Guid.NewGuid(), null);

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task WithdrawJoinRequestAsync_InvalidatesCache()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();
        await _service.GetTeamAsync(team.Id);
        var before = _service.BulkInvalidations;

        await _service.WithdrawJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid());

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_InvalidatesCache()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();
        await _service.GetTeamAsync(team.Id);
        var before = _service.BulkInvalidations;

        await _service.RejectJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid(), "reason");

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task ApproveJoinRequestAsync_InvalidatesCache()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();
        await _service.GetTeamAsync(team.Id);
        var before = _service.BulkInvalidations;

        // Inner is an unconfigured NSubstitute mock; ApproveJoinRequestAsync
        // returns default (null TeamMember) — that's fine for this assertion.
        await _service.ApproveJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid(), null);

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task WarmAllAsync_PopulatesTeamInfoPendingRequestCount()
    {
        var team = SeedTeam("Alpha");
        SeedJoinRequest(team.Id, SeedUser("Requester A").Id);
        SeedJoinRequest(team.Id, SeedUser("Requester B").Id);
        await _dbContext.SaveChangesAsync();

        var info = await _service.GetTeamAsync(team.Id);

        info.Should().NotBeNull();
        info.PendingRequestCount.Should().Be(2);
    }

    private TeamJoinRequest SeedJoinRequest(Guid teamId, Guid userId)
    {
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamJoinRequests.Add(request);
        return request;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }
}
