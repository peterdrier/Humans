using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TeamRoleServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly TeamService _service;

    public TeamRoleServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 11, 12, 0));
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var roleAssignmentService = new RoleAssignmentService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<ISystemTeamSync>(),
            _clock,
            cache,
            NullLogger<RoleAssignmentService>.Instance);
        var shiftManagementService = new ShiftManagementService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            cache,
            _clock,
            NullLogger<ShiftManagementService>.Instance);
        _service = new TeamService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<INotificationService>(),
            roleAssignmentService,
            shiftManagementService,
            Substitute.For<ISystemTeamSync>(),
            _clock,
            cache,
            NullLogger<TeamService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // CreateRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task CreateRoleDefinitionAsync_SystemTeam_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.CreateRoleDefinitionAsync(
            team.Id, "Designer", null, 2,
            [SlotPriority.Critical, SlotPriority.Important], 1, RolePeriod.YearRound, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [Fact]
    public async Task CreateRoleDefinitionAsync_ValidInput_CreatesDefinition()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        await _dbContext.SaveChangesAsync();

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Designer", "Designs things", 2,
            [SlotPriority.Critical, SlotPriority.Important], 1, RolePeriod.YearRound, admin.Id);

        result.Should().NotBeNull();
        result.Name.Should().Be("Designer");
        result.Description.Should().Be("Designs things");
        result.SlotCount.Should().Be(2);
        result.TeamId.Should().Be(team.Id);
        result.SortOrder.Should().Be(1);
        result.Priorities.Should().HaveCount(2);

        var inDb = await _dbContext.Set<TeamRoleDefinition>()
            .FirstOrDefaultAsync(d => d.Id == result.Id);
        inDb.Should().NotBeNull();
    }

    // ==========================================================================
    // DeleteRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task DeleteRoleDefinitionAsync_ManagementRoleWithAssignments_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 1, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user);
        SeedRoleAssignment(mgmtRole, member, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.DeleteRoleDefinitionAsync(mgmtRole.Id, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*management role*");
    }

    // ==========================================================================
    // UpdateRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task UpdateRoleDefinitionAsync_CannotReduceSlotsBelowFilled()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user1 = SeedUser("User1");
        var user2 = SeedUser("User2");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        var member1 = SeedMember(team, user1);
        var member2 = SeedMember(team, user2);
        SeedRoleAssignment(role, member1, slotIndex: 0);
        SeedRoleAssignment(role, member2, slotIndex: 1);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            role.Id, "Designer", null, 1,
            [SlotPriority.Critical], 1, false, RolePeriod.YearRound, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot reduce slot count*");
    }

    [Fact]
    public async Task UpdateRoleDefinitionAsync_SetIsManagement_WhenAnotherRoleAlreadyManagement_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var existingMgmt = SeedRoleDefinition(team, "Coordinator", slotCount: 1, sortOrder: 0, isManagement: true);
        var otherRole = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            otherRole.Id, "Designer", null, 2,
            [SlotPriority.Critical, SlotPriority.Important], 1, true, RolePeriod.YearRound, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already marked as the management role*");
    }

    [Fact]
    public async Task UpdateRoleDefinitionAsync_SetIsManagement_WhenNoOtherManagement_Succeeds()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var role = SeedRoleDefinition(team, "Lead", slotCount: 1, sortOrder: 0);
        await _dbContext.SaveChangesAsync();

        var result = await _service.UpdateRoleDefinitionAsync(
            role.Id, "Lead", null, 1,
            [SlotPriority.Critical], 0, true, RolePeriod.YearRound, admin.Id);

        result.IsManagement.Should().BeTrue();
    }

    // ==========================================================================
    // SetRoleIsManagementAsync
    // ==========================================================================

    [Fact]
    public async Task SetRoleIsManagementAsync_ClearWithAssignedMembers_DemotesCoordinators()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user, TeamMemberRole.Coordinator);
        SeedRoleAssignment(mgmtRole, member, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        await _service.SetRoleIsManagementAsync(mgmtRole.Id, false, admin.Id);

        var roleInDb = await _dbContext.Set<TeamRoleDefinition>().FindAsync(mgmtRole.Id);
        roleInDb!.IsManagement.Should().BeFalse();

        var memberInDb = await _dbContext.TeamMembers.FindAsync(member.Id);
        memberInDb!.Role.Should().Be(TeamMemberRole.Member);
    }

    [Fact]
    public async Task SetRoleIsManagementAsync_SetWithAssignedMembers_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        var member = SeedMember(team, user);
        SeedRoleAssignment(role, member, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.SetRoleIsManagementAsync(role.Id, true, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot set IsManagement*");
    }

    // ==========================================================================
    // AssignToRoleAsync
    // ==========================================================================

    [Fact]
    public async Task AssignToRoleAsync_ValidMember_CreatesAssignment()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        SeedMember(team, user);
        await _dbContext.SaveChangesAsync();

        var result = await _service.AssignToRoleAsync(role.Id, user.Id, admin.Id);

        result.Should().NotBeNull();
        result.TeamRoleDefinitionId.Should().Be(role.Id);
        result.SlotIndex.Should().Be(0);

        var inDb = await _dbContext.Set<TeamRoleAssignment>()
            .FirstOrDefaultAsync(a => a.Id == result.Id);
        inDb.Should().NotBeNull();
    }

    [Fact]
    public async Task AssignToRoleAsync_NonMember_AutoAddsToTeam()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        // Deliberately not adding user as team member
        await _dbContext.SaveChangesAsync();

        var result = await _service.AssignToRoleAsync(role.Id, user.Id, admin.Id);

        result.Should().NotBeNull();
        result.TeamRoleDefinitionId.Should().Be(role.Id);

        // Verify user was auto-added to team
        var memberInDb = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == user.Id && tm.LeftAt == null);
        memberInDb.Should().NotBeNull();
    }

    [Fact]
    public async Task AssignToRoleAsync_AllSlotsFilled_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user1 = SeedUser("User1");
        var user2 = SeedUser("User2");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 1, sortOrder: 1);
        var member1 = SeedMember(team, user1);
        SeedMember(team, user2);
        SeedRoleAssignment(role, member1, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.AssignToRoleAsync(role.Id, user2.Id, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*slots*filled*");
    }

    [Fact]
    public async Task AssignToRoleAsync_ManagementRole_SetsTeamMemberRoleToCoordinator()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user);
        await _dbContext.SaveChangesAsync();

        await _service.AssignToRoleAsync(mgmtRole.Id, user.Id, admin.Id);

        var memberInDb = await _dbContext.TeamMembers.FindAsync(member.Id);
        memberInDb!.Role.Should().Be(TeamMemberRole.Coordinator);
    }

    // ==========================================================================
    // UnassignFromRoleAsync
    // ==========================================================================

    [Fact]
    public async Task UnassignFromRoleAsync_NonManagementRole_RemovesAssignment()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        var member = SeedMember(team, user);
        SeedRoleAssignment(role, member, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        await _service.UnassignFromRoleAsync(role.Id, member.Id, admin.Id);

        var assignments = await _dbContext.Set<TeamRoleAssignment>()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync();
        assignments.Should().BeEmpty();
    }

    [Fact]
    public async Task UnassignFromRoleAsync_OnlyManagementAssignment_DemotesToMember()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user, TeamMemberRole.Coordinator);
        SeedRoleAssignment(mgmtRole, member, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        await _service.UnassignFromRoleAsync(mgmtRole.Id, member.Id, admin.Id);

        var memberInDb = await _dbContext.TeamMembers.FindAsync(member.Id);
        memberInDb!.Role.Should().Be(TeamMemberRole.Member);
    }

    [Fact]
    public async Task UnassignFromRoleAsync_MultipleManagementAssignments_KeepsCoordinatorRole()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team1 = SeedTeam("Team A");
        var team2 = SeedTeam("Team B");
        var user = SeedUser("User");
        var mgmtRole1 = SeedRoleDefinition(team1, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var mgmtRole2 = SeedRoleDefinition(team2, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member1 = SeedMember(team1, user, TeamMemberRole.Coordinator);
        var member2 = SeedMember(team2, user, TeamMemberRole.Coordinator);
        SeedRoleAssignment(mgmtRole1, member1, slotIndex: 0);
        SeedRoleAssignment(mgmtRole2, member2, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        // Unassign from team1's management role — but still coordinator on team2
        await _service.UnassignFromRoleAsync(mgmtRole1.Id, member1.Id, admin.Id);

        // member1's Role demotes because the demotion check uses TeamMemberId,
        // and member1 and member2 are different TeamMember entities.
        // member1 has no other management assignments → demotes.
        var member1InDb = await _dbContext.TeamMembers.FindAsync(member1.Id);
        member1InDb!.Role.Should().Be(TeamMemberRole.Member);

        // member2 is unaffected
        var member2InDb = await _dbContext.TeamMembers.FindAsync(member2.Id);
        member2InDb!.Role.Should().Be(TeamMemberRole.Coordinator);
    }

    // ==========================================================================
    // LeaveTeamAsync — role assignment cleanup
    // ==========================================================================

    [Fact]
    public async Task LeaveTeamAsync_CleansUpRoleAssignments()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, "Designer", slotCount: 2, sortOrder: 1);
        var member = SeedMember(team, user);
        SeedRoleAssignment(role, member, slotIndex: 0);
        await _dbContext.SaveChangesAsync();

        await _service.LeaveTeamAsync(team.Id, user.Id);

        var assignments = await _dbContext.Set<TeamRoleAssignment>()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync();
        assignments.Should().BeEmpty();

        var memberInDb = await _dbContext.TeamMembers.FindAsync(member.Id);
        memberInDb!.LeftAt.Should().NotBeNull();
    }

    // ==========================================================================
    // Seed Helpers
    // ==========================================================================

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

    private Team SeedTeam(string name = "Test Team", SystemTeamType type = SystemTeamType.None)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            SystemTeamType = type,
            IsActive = true,
            RequiresApproval = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private TeamMember SeedMember(Team team, User user, TeamMemberRole role = TeamMemberRole.Member)
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
        return member;
    }

    private TeamRoleDefinition SeedRoleDefinition(Team team, string name = "Designer",
        int slotCount = 2, int sortOrder = 1, bool isManagement = false)
    {
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Name = name,
            SlotCount = slotCount,
            IsManagement = isManagement,
            Priorities = Enumerable.Range(0, slotCount)
                .Select(i => i == 0 ? SlotPriority.Critical : SlotPriority.Important)
                .ToList(),
            SortOrder = sortOrder,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Set<TeamRoleDefinition>().Add(definition);
        return definition;
    }

    private void SeedAdminRole(User user)
    {
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleName = RoleNames.Admin,
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(1),
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = user.Id
        });
    }

    private void SeedRoleAssignment(TeamRoleDefinition definition, TeamMember member, int slotIndex)
    {
        _dbContext.Set<TeamRoleAssignment>().Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = definition.Id,
            TeamMemberId = member.Id,
            SlotIndex = slotIndex,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = member.UserId
        });
    }
}
