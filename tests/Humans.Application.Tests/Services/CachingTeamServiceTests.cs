using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class CachingTeamServiceTests : ServiceTestHarness
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private readonly ServiceProvider _serviceProvider;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ITeamService _innerTeamService;
    private readonly CachingTeamService _service;

    public CachingTeamServiceTests()
    {
        var userService = NewDbBackedUserService();

        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        _roleAssignmentService
            .IsUserBoardMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _innerTeamService = Substitute.For<ITeamService>();
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => BuildTeamInfosAsync());

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton<IUserServiceRead>(userService);
        services.AddSingleton(_roleAssignmentService);
        services.AddKeyedScoped<ITeamService>(
            CachingTeamService.InnerServiceKey,
            (_, _) => _innerTeamService);
        _serviceProvider = services.BuildServiceProvider();

        _service = new CachingTeamService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTeamService>.Instance);
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_InactiveDirectCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var inactiveTeam = SeedTeam("Inactive", isActive: false);
        SeedTeamMember(inactiveTeam.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

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
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        var member = result!.Members.Should().ContainSingle().Subject;
        member.Email.Should().Be("alice@example.test");
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_AfterWarmup_ReturnsAllActiveMemberships()
    {
        var user = SeedUser("Alice");
        var team1 = SeedTeam("Alpha");
        var team2 = SeedTeam("Beta");
        SeedTeamMember(team1.Id, user.Id, TeamMemberRole.Member);
        SeedTeamMember(team2.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

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
        member.LeftAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

        // Warm the cache + inverse index.
        var before = await _service.GetUserTeamsAsync(user.Id);
        before.Should().HaveCount(1);

        // New membership written outside the decorator: clearing simulates the
        // real path where mutation methods call InvalidateTeamsCache().
        var team2 = SeedTeam("Beta");
        SeedTeamMember(team2.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();
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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();

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
        await Db.SaveChangesAsync();
        await _service.GetTeamAsync(team.Id);
        var before = _service.BulkInvalidations;

        await _service.WithdrawJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid());

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_InvalidatesCache()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();
        await _service.GetTeamAsync(team.Id);
        var before = _service.BulkInvalidations;

        await _service.RejectJoinRequestAsync(Guid.NewGuid(), Guid.NewGuid(), "reason");

        _service.BulkInvalidations.Should().BeGreaterThan(before);
    }

    [HumansFact]
    public async Task ApproveJoinRequestAsync_InvalidatesCache()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();
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
        await Db.SaveChangesAsync();

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
            RequestedAt = Clock.GetCurrentInstant()
        };
        Db.TeamJoinRequests.Add(request);
        return request;
    }

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> BuildTeamInfosAsync()
    {
        var teams = await Db.Teams.AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync();
        var userIds = teams
            .SelectMany(t => t.Members.Select(m => m.UserId))
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, User>()
            : await Db.Users.AsNoTracking()
                .Include(u => u.UserEmails)
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id);
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var pendingRows = await Db.TeamJoinRequests.AsNoTracking()
            .Where(r => r.Status == TeamJoinRequestStatus.Pending)
            .ToListAsync();
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
                            m.JoinedAt,
                            user?.GoogleEmailStatus ?? GoogleEmailStatus.Unknown);
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
