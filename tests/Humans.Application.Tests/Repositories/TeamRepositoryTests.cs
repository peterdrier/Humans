// TeamMember.User / TeamJoinRequest.User are Obsolete per §6c; tests seed
// them on raw entities before SaveChanges.
#pragma warning disable CS0618
using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Teams;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Repository tests for the Teams section — issue #540a (§15 Part 1 —
/// TeamService core). Covers the bundled-write paths (which have compound
/// mutations across TeamMembers / TeamJoinRequests / TeamRoleAssignments /
/// GoogleSyncOutboxEvents) plus the narrow read shapes the service depends
/// on for cross-section stitching.
/// </summary>
public sealed class TeamRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly TeamRepository _repo;

    public TeamRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new TeamRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // Team reads
    // ==========================================================================

    [HumansFact]
    public async Task GetByIdAsync_ReturnsTeam_WhenPresent()
    {
        var team = await SeedTeamAsync("Test");

        var result = await _repo.GetByIdAsync(team.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(team.Id);
        result.Name.Should().Be("Test");
    }

    [HumansFact]
    public async Task GetByIdAsync_ReturnsNull_WhenMissing()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetByIdWithRelationsAsync_LoadsActiveMembersAndChildren()
    {
        var team = await SeedTeamAsync("Dept");
        var user = await SeedUserAsync();
        await SeedActiveMemberAsync(team, user);
        await SeedTeamAsync("Sub", parentTeamId: team.Id);

        var result = await _repo.GetByIdWithRelationsAsync(team.Id);

        result.Should().NotBeNull();
        result!.Members.Should().HaveCount(1);
        result.ChildTeams.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task SlugExistsAsync_ReturnsTrue_WhenSlugMatches()
    {
        await SeedTeamAsync("Test", slug: "test");

        var exists = await _repo.SlugExistsAsync("test", excludingTeamId: null);

        exists.Should().BeTrue();
    }

    [HumansFact]
    public async Task SlugExistsAsync_ReturnsFalse_WhenSlugIsOwnTeam()
    {
        var team = await SeedTeamAsync("Test", slug: "test");

        var exists = await _repo.SlugExistsAsync("test", excludingTeamId: team.Id);

        exists.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetAllActiveAsync_ExcludesInactiveTeams()
    {
        await SeedTeamAsync("Active", isActive: true);
        await SeedTeamAsync("Inactive", isActive: false);

        var all = await _repo.GetAllActiveAsync();

        all.Should().ContainSingle(t => t.Name == "Active");
    }

    [HumansFact]
    public async Task GetActiveOptionsAsync_ReturnsIdNamePairs()
    {
        await SeedTeamAsync("A");
        await SeedTeamAsync("B");

        var options = await _repo.GetActiveOptionsAsync();

        options.Select(o => o.Name).Should().Contain(["A", "B"]);
    }

    [HumansFact]
    public async Task GetNamesByIdsAsync_IncludesInactive_ForGdprHistory()
    {
        var active = await SeedTeamAsync("Active", isActive: true);
        var inactive = await SeedTeamAsync("Inactive", isActive: false);

        var names = await _repo.GetNamesByIdsAsync(new[] { active.Id, inactive.Id });

        names[active.Id].Should().Be("Active");
        names[inactive.Id].Should().Be("Inactive");
    }

    // ==========================================================================
    // Membership writes — compound transactions
    // ==========================================================================

    [HumansFact]
    public async Task AddMemberWithOutboxAsync_PersistsBothInSameTransaction()
    {
        var team = await SeedTeamAsync("Test");
        var user = await SeedUserAsync();

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };
        var outbox = new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            EventType = Humans.Domain.Constants.GoogleSyncOutboxEventTypes.AddUserToTeamResources,
            OccurredAt = _clock.GetCurrentInstant(),
            DeduplicationKey = $"{member.Id}:AddUserToTeamResources"
        };

        var ok = await _repo.AddMemberWithOutboxAsync(member, outbox);

        ok.Should().BeTrue();
        (await _dbContext.TeamMembers.CountAsync()).Should().Be(1);
        (await _dbContext.GoogleSyncOutboxEvents.CountAsync()).Should().Be(1);
    }

    [HumansFact]
    public async Task MarkMemberLeftWithOutboxAsync_SetsLeftAtAndRemovesAssignments()
    {
        var team = await SeedTeamAsync("Test");
        var user = await SeedUserAsync();
        var member = await SeedActiveMemberAsync(team, user);
        var role = await SeedRoleDefinitionAsync(team, isManagement: true);
        await SeedRoleAssignmentAsync(role, member);

        var now = _clock.GetCurrentInstant();
        var outbox = new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            EventType = Humans.Domain.Constants.GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources,
            OccurredAt = now,
            DeduplicationKey = $"{member.Id}:RemoveUserFromTeamResources"
        };

        var removed = await _repo.MarkMemberLeftWithOutboxAsync(member.Id, now, outbox);

        removed.Should().HaveCount(1);
        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.TeamMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id);
        reloaded.LeftAt.Should().Be(now);
        (await _dbContext.Set<TeamRoleAssignment>().CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task WithdrawRequestAsync_ReturnsFalse_WhenRequestNotPending()
    {
        var team = await SeedTeamAsync("Test");
        var user = await SeedUserAsync();
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Status = TeamJoinRequestStatus.Approved,
            RequestedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamJoinRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        var withdrew = await _repo.WithdrawRequestAsync(request.Id, user.Id, _clock.GetCurrentInstant());

        withdrew.Should().BeFalse();
    }

    [HumansFact]
    public async Task WithdrawRequestAsync_SetsStatusAndResolvedAt_WhenPending()
    {
        var team = await SeedTeamAsync("Test");
        var user = await SeedUserAsync();
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamJoinRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        var now = _clock.GetCurrentInstant();
        var withdrew = await _repo.WithdrawRequestAsync(request.Id, user.Id, now);

        withdrew.Should().BeTrue();
        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.TeamJoinRequests.AsNoTracking().FirstAsync(r => r.Id == request.Id);
        reloaded.Status.Should().Be(TeamJoinRequestStatus.Withdrawn);
        reloaded.ResolvedAt.Should().Be(now);
    }

    [HumansFact]
    public async Task DeactivateTeamAsync_SoftDeletesAndClosesActiveMemberships()
    {
        var team = await SeedTeamAsync("Test");
        var user = await SeedUserAsync();
        var member = await SeedActiveMemberAsync(team, user);

        var now = _clock.GetCurrentInstant();
        var count = await _repo.DeactivateTeamAsync(team.Id, now);

        count.Should().Be(1);
        _dbContext.ChangeTracker.Clear();
        var t = await _dbContext.Teams.AsNoTracking().FirstAsync(x => x.Id == team.Id);
        t.IsActive.Should().BeFalse();
        var m = await _dbContext.TeamMembers.AsNoTracking().FirstAsync(x => x.Id == member.Id);
        m.LeftAt.Should().Be(now);
    }

    [HumansFact]
    public async Task RevokeAllMembershipsAsync_ClosesEveryActiveMembershipAndRemovesAssignments()
    {
        var team1 = await SeedTeamAsync("Team A");
        var team2 = await SeedTeamAsync("Team B");
        var user = await SeedUserAsync();
        var m1 = await SeedActiveMemberAsync(team1, user);
        var m2 = await SeedActiveMemberAsync(team2, user);
        var role = await SeedRoleDefinitionAsync(team1, isManagement: true);
        await SeedRoleAssignmentAsync(role, m1);

        var now = _clock.GetCurrentInstant();
        var count = await _repo.RevokeAllMembershipsAsync(user.Id, now);

        count.Should().Be(2);
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.TeamMembers.AsNoTracking().CountAsync(m => m.UserId == user.Id && m.LeftAt == null)).Should().Be(0);
        (await _dbContext.Set<TeamRoleAssignment>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<Team> SeedTeamAsync(
        string name,
        string? slug = null,
        bool isActive = true,
        Guid? parentTeamId = null)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug ?? name.ToLowerInvariant(),
            IsActive = isActive,
            ParentTeamId = parentTeamId,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();
        return team;
    }

    private async Task<User> SeedUserAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"user-{Guid.NewGuid():N}@example.com",
            Email = $"user-{Guid.NewGuid():N}@example.com",
            DisplayName = "Seeded User",
            CreatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task<TeamMember> SeedActiveMemberAsync(Team team, User user, TeamMemberRole role = TeamMemberRole.Member)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        return member;
    }

    private async Task<TeamRoleDefinition> SeedRoleDefinitionAsync(Team team, bool isManagement)
    {
        var def = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Name = "Coord",
            SlotCount = 1,
            Priorities = [SlotPriority.Critical],
            SortOrder = 0,
            IsManagement = isManagement,
            IsPublic = true,
            Period = RolePeriod.YearRound,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Set<TeamRoleDefinition>().Add(def);
        await _dbContext.SaveChangesAsync();
        return def;
    }

    private async Task<TeamRoleAssignment> SeedRoleAssignmentAsync(TeamRoleDefinition def, TeamMember member)
    {
        var assignment = new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = def.Id,
            TeamMemberId = member.Id,
            SlotIndex = 0,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = member.UserId
        };
        _dbContext.Set<TeamRoleAssignment>().Add(assignment);
        await _dbContext.SaveChangesAsync();
        return assignment;
    }
}
