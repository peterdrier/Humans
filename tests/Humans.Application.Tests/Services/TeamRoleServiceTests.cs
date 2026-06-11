using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Repositories.Teams;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;
using TeamService = Humans.Application.Services.Teams.TeamService;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Services.GoogleIntegration;
using Humans.Application.Interfaces.Auth;
using Humans.Infrastructure.Repositories.Auth;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Humans.Infrastructure.Repositories.Shifts;

namespace Humans.Application.Tests.Services;

public sealed class TeamRoleServiceTests : ServiceTestHarness
{
    private readonly TeamService _service;

    public TeamRoleServiceTests() : base(Instant.FromUtc(2026, 3, 11, 12, 0))
    {
        var roleAssignmentService = new RoleAssignmentService(
            new RoleAssignmentRepository(DbFactory),
            Substitute.For<IUserService>(),
            AuditLog,
            Notifier,
            Substitute.For<ISystemTeamSync>(),
            Substitute.For<INavBadgeCacheInvalidator>(),
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            Substitute.For<IRoleAssignmentCacheInvalidator>(),
            Clock,
            NullLogger<RoleAssignmentService>.Instance);
        var teamResourceService = Substitute.For<ITeamResourceService>();
        teamResourceService
            .GetTeamResourceSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamResourceSummary>());
        var userService = NewDbBackedUserService();
        var googleOutboxService = new GoogleSyncOutboxService(
            new GoogleSyncOutboxRepository(DbFactory));
        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamService>()
            .With<IRoleAssignmentService>(roleAssignmentService)
            .With<IEmailService>()
            .With<ISystemTeamSync>()
            .With<IGoogleSyncOutboxService>(googleOutboxService)
            .With(teamResourceService)
            .With(userService)
            .Build();
        var shiftManagementService = new ShiftManagementService(
            new ShiftRepository(DbFactory, Db, Clock),
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock);
        _service = new TeamService(
            new TeamRepository(DbFactory),
            AuditLog,
            Notifier,
            shiftManagementService,
            Substitute.For<INotificationMeterCacheInvalidator>(),
            ShiftAuthInvalidator,
            AdminAuthorization,
            EarlyEntryInvalidator,
            serviceProvider,
            Clock,
            NullLogger<TeamService>.Instance);
    }

    // ==========================================================================
    // CreateRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_ValidInput_CreatesDefinition()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Designer", "Designs things", 2,
            [SlotPriority.Critical, SlotPriority.Important], 1, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Name.Should().Be("Designer");
        result.Description.Should().Be("Designs things");
        result.SlotCount.Should().Be(2);
        result.TeamId.Should().Be(team.Id);
        result.SortOrder.Should().Be(1);
        result.Priorities.Should().HaveCount(2);

        var inDb = await Db.Set<TeamRoleDefinition>()
            .FirstOrDefaultAsync(d => d.Id == result.Id, Xunit.TestContext.Current.CancellationToken);
        inDb.Should().NotBeNull();
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_WithEstimatedHours_PersistsValue()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Coordinator", null, 1,
            [SlotPriority.Critical], 0, RolePeriod.YearRound, admin.Id,
            estimatedHours: 120, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.EstimatedHours.Should().Be(120);

        var inDb = await Db.Set<TeamRoleDefinition>().FirstAsync(d => d.Id == result.Id, Xunit.TestContext.Current.CancellationToken);
        inDb.EstimatedHours.Should().Be(120);
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_WithoutEstimatedHours_DefaultsToNull()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Coordinator", null, 1,
            [SlotPriority.Critical], 0, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.EstimatedHours.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_SetThenClearEstimatedHours_RoundTrips()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var created = await _service.CreateRoleDefinitionAsync(
            team.Id, "Coordinator", null, 1,
            [SlotPriority.Critical], 0, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var set = await _service.UpdateRoleDefinitionAsync(
            created.Id, "Coordinator", null, 1,
            [SlotPriority.Critical], 0, false, RolePeriod.YearRound, admin.Id,
            estimatedHours: 80, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        set.EstimatedHours.Should().Be(80);

        var cleared = await _service.UpdateRoleDefinitionAsync(
            created.Id, "Coordinator", null, 1,
            [SlotPriority.Critical], 0, false, RolePeriod.YearRound, admin.Id,
            estimatedHours: null, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        cleared.EstimatedHours.Should().BeNull();
    }

    [HumansFact]
    public async Task GetRoleDefinitionsAsync_SurfacesEstimatedHoursInSnapshot()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.CreateRoleDefinitionAsync(
            team.Id, "Coordinator", null, 1,
            [SlotPriority.Critical], 0, RolePeriod.YearRound, admin.Id,
            estimatedHours: 200, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var snapshots = await _service.GetRoleDefinitionsAsync(team.Id, Xunit.TestContext.Current.CancellationToken);

        snapshots.Should().ContainSingle(s => s.Name == "Coordinator")
            .Which.EstimatedHours.Should().Be(200);
    }

    // ==========================================================================
    // DeleteRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_ManagementRoleWithAssignments_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 1, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user);
        SeedRoleAssignment(mgmtRole, member, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.DeleteRoleDefinitionAsync(mgmtRole.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*management role*");
    }

    // ==========================================================================
    // UpdateRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_CannotReduceSlotsBelowFilled()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user1 = SeedUser("User1");
        var user2 = SeedUser("User2");
        var role = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        var member1 = SeedMember(team, user1);
        var member2 = SeedMember(team, user2);
        SeedRoleAssignment(role, member1, slotIndex: 0);
        SeedRoleAssignment(role, member2, slotIndex: 1);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.UpdateRoleDefinitionAsync(
            role.Id, "Designer", null, 1,
            [SlotPriority.Critical], 1, false, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot reduce slot count*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_SetIsManagement_WhenAnotherRoleAlreadyManagement_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        SeedRoleDefinition(team, "Coordinator", slotCount: 1, sortOrder: 0, isManagement: true);
        var otherRole = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.UpdateRoleDefinitionAsync(
            otherRole.Id, "Designer", null, 2,
            [SlotPriority.Critical, SlotPriority.Important], 1, true, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already marked as the management role*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_SetIsManagement_WhenNoOtherManagement_Succeeds()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var role = SeedRoleDefinition(team, "Lead", slotCount: 1, sortOrder: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.UpdateRoleDefinitionAsync(
            role.Id, "Lead", null, 1,
            [SlotPriority.Critical], 0, true, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.IsManagement.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_WhenCallerCannotToggleManagement_PreservesExistingValue()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var role = SeedRoleDefinition(team, "Lead", slotCount: 1, sortOrder: 0, isManagement: true);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.UpdateRoleDefinitionAsync(
            role.Id, "Lead", null, 1,
            [SlotPriority.Critical], 0, false, RolePeriod.YearRound, admin.Id,
            canToggleManagement: false, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.IsManagement.Should().BeTrue();
    }

    /// <summary>
    /// Regression: Codex PR#300 P1 — when <c>IsManagement</c> flips on a role
    /// with existing assignments, the service must invalidate shift
    /// authorization for every assignee's user id. The bug was that
    /// <c>FindRoleDefinitionForMutationAsync</c> returned assignments without
    /// <c>TeamMember</c> loaded, so the computed user-id set was empty and the
    /// cache stayed stale. Repository now eager-loads
    /// <c>Assignments.TeamMember</c>.
    /// </summary>
    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_FlipIsManagementWithAssignees_InvalidatesShiftAuthPerAssignee()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user1 = SeedUser("U1");
        var user2 = SeedUser("U2");
        var role = SeedRoleDefinition(team, "Lead", slotCount: 2, sortOrder: 0);
        var member1 = SeedMember(team, user1);
        var member2 = SeedMember(team, user2);
        SeedRoleAssignment(role, member1, slotIndex: 0);
        SeedRoleAssignment(role, member2, slotIndex: 1);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Flip IsManagement from false -> true while the role has assignees.
        await _service.UpdateRoleDefinitionAsync(
            role.Id, "Lead", null, 2,
            [SlotPriority.Critical, SlotPriority.Critical], 0,
            isManagement: true, RolePeriod.YearRound, admin.Id, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        ShiftAuthInvalidator.Received(1).Invalidate(user1.Id);
        ShiftAuthInvalidator.Received(1).Invalidate(user2.Id);
    }

    // ==========================================================================
    // ToggleRoleIsManagementAsync
    // ==========================================================================

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_ClearWithAssignedMembers_DemotesCoordinators()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user, TeamMemberRole.Coordinator);
        SeedRoleAssignment(mgmtRole, member, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.ToggleRoleIsManagementAsync(mgmtRole.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        result.IsManagement.Should().BeFalse();

        Db.ChangeTracker.Clear();

        var roleInDb = await Db.Set<TeamRoleDefinition>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == mgmtRole.Id, Xunit.TestContext.Current.CancellationToken);
        roleInDb!.IsManagement.Should().BeFalse();

        var memberInDb = await Db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        memberInDb!.Role.Should().Be(TeamMemberRole.Member);
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_SetWithAssignedMembers_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        var member = SeedMember(team, user);
        SeedRoleAssignment(role, member, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.ToggleRoleIsManagementAsync(role.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot set IsManagement*");
    }

    // ==========================================================================
    // AssignToRoleAsync
    // ==========================================================================

    [HumansFact]
    public async Task AssignToRoleAsync_ValidMember_CreatesAssignment()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        SeedMember(team, user);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.AssignToRoleAsync(role.Id, user.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.TeamRoleDefinitionId.Should().Be(role.Id);
        result.SlotIndex.Should().Be(0);

        var inDb = await Db.Set<TeamRoleAssignment>()
            .FirstOrDefaultAsync(a => a.Id == result.Id, Xunit.TestContext.Current.CancellationToken);
        inDb.Should().NotBeNull();
    }

    [HumansFact(Timeout = 10000)]
    public async Task AssignToRoleAsync_NonMember_AutoAddsToTeam()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        // Deliberately not adding user as team member
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.AssignToRoleAsync(role.Id, user.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.TeamRoleDefinitionId.Should().Be(role.Id);

        // Verify user was auto-added to team
        var memberInDb = await Db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == user.Id && tm.LeftAt == null, Xunit.TestContext.Current.CancellationToken);
        memberInDb.Should().NotBeNull();
    }

    [HumansFact]
    public async Task AssignToRoleAsync_NonMemberOnSystemTeam_Throws()
    {
        // System-team membership is sync-managed; role assignment must not be a
        // backdoor for injecting non-members (and firing Google sync side effects).
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Board", type: SystemTeamType.Board);
        var user = SeedUser("Outsider");
        var role = SeedRoleDefinition(team, "President", slotCount: 1, sortOrder: 1);
        // Deliberately not adding user as team member
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.AssignToRoleAsync(role.Id, user.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");

        var memberInDb = await Db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == user.Id && tm.LeftAt == null, Xunit.TestContext.Current.CancellationToken);
        memberInDb.Should().BeNull();
    }

    [HumansFact]
    public async Task AssignToRoleAsync_ExistingMemberOnSystemTeam_CreatesAssignment()
    {
        // The legitimate case: synced members of a system team (e.g. Board) can be
        // assigned to roles on that team without tripping the auto-add guard.
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Board", type: SystemTeamType.Board);
        var user = SeedUser("BoardMember");
        var role = SeedRoleDefinition(team, "President", slotCount: 1, sortOrder: 1);
        SeedMember(team, user);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.AssignToRoleAsync(role.Id, user.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.TeamRoleDefinitionId.Should().Be(role.Id);
    }

    [HumansFact]
    public async Task AssignToRoleAsync_AllSlotsFilled_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user1 = SeedUser("User1");
        var user2 = SeedUser("User2");
        var role = SeedRoleDefinition(team, slotCount: 1, sortOrder: 1);
        var member1 = SeedMember(team, user1);
        SeedMember(team, user2);
        SeedRoleAssignment(role, member1, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var act = () => _service.AssignToRoleAsync(role.Id, user2.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*slots*filled*");
    }

    [HumansFact]
    public async Task AssignToRoleAsync_ManagementRole_SetsTeamMemberRoleToCoordinator()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.AssignToRoleAsync(mgmtRole.Id, user.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        Db.ChangeTracker.Clear();

        var memberInDb = await Db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        memberInDb!.Role.Should().Be(TeamMemberRole.Coordinator);
    }

    // ==========================================================================
    // UnassignFromRoleAsync
    // ==========================================================================

    [HumansFact]
    public async Task UnassignFromRoleAsync_NonManagementRole_RemovesAssignment()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        var member = SeedMember(team, user);
        SeedRoleAssignment(role, member, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.UnassignFromRoleAsync(role.Id, member.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        var assignments = await Db.Set<TeamRoleAssignment>()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UnassignFromRoleAsync_OnlyManagementAssignment_DemotesToMember()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var mgmtRole = SeedRoleDefinition(team, "Coordinator", slotCount: 2, sortOrder: 0, isManagement: true);
        var member = SeedMember(team, user, TeamMemberRole.Coordinator);
        SeedRoleAssignment(mgmtRole, member, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.UnassignFromRoleAsync(mgmtRole.Id, member.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        Db.ChangeTracker.Clear();

        var memberInDb = await Db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        memberInDb!.Role.Should().Be(TeamMemberRole.Member);
    }

    [HumansFact]
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Unassign from team1's management role — but still coordinator on team2
        await _service.UnassignFromRoleAsync(mgmtRole1.Id, member1.Id, admin.Id, Xunit.TestContext.Current.CancellationToken);

        Db.ChangeTracker.Clear();

        // member1's Role demotes because the demotion check uses TeamMemberId,
        // and member1 and member2 are different TeamMember entities.
        // member1 has no other management assignments → demotes.
        var member1InDb = await Db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == member1.Id, Xunit.TestContext.Current.CancellationToken);
        member1InDb!.Role.Should().Be(TeamMemberRole.Member);

        // member2 is unaffected
        var member2InDb = await Db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == member2.Id, Xunit.TestContext.Current.CancellationToken);
        member2InDb!.Role.Should().Be(TeamMemberRole.Coordinator);
    }

    // ==========================================================================
    // LeaveTeamAsync — role assignment cleanup
    // ==========================================================================

    [HumansFact]
    public async Task LeaveTeamAsync_CleansUpRoleAssignments()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam("Test Team");
        var user = SeedUser("User");
        var role = SeedRoleDefinition(team, slotCount: 2, sortOrder: 1);
        var member = SeedMember(team, user);
        SeedRoleAssignment(role, member, slotIndex: 0);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.LeaveTeamAsync(team.Id, user.Id, Xunit.TestContext.Current.CancellationToken);

        // Service now persists via its own DbContext; detach the tracker so we
        // re-read from the store rather than seeing the stale in-memory entity.
        Db.ChangeTracker.Clear();

        var assignments = await Db.Set<TeamRoleAssignment>()
            .AsNoTracking()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        assignments.Should().BeEmpty();

        var memberInDb = await Db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        memberInDb!.LeftAt.Should().NotBeNull();
    }

    // ==========================================================================
    // Seed Helpers
    // ==========================================================================

    private TeamMember SeedMember(Team team, User user, TeamMemberRole role = TeamMemberRole.Member)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Role = role,
            JoinedAt = Clock.GetCurrentInstant()
        };
        Db.TeamMembers.Add(member);
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.Set<TeamRoleDefinition>().Add(definition);
        return definition;
    }

    private void SeedAdminRole(User user)
    {
        Db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleName = RoleNames.Admin,
            ValidFrom = Clock.GetCurrentInstant() - Duration.FromDays(1),
            CreatedAt = Clock.GetCurrentInstant(),
            CreatedByUserId = user.Id
        });
    }

    private void SeedRoleAssignment(TeamRoleDefinition definition, TeamMember member, int slotIndex)
    {
        Db.Set<TeamRoleAssignment>().Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = definition.Id,
            TeamMemberId = member.Id,
            SlotIndex = slotIndex,
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = member.UserId
        });
    }
}
