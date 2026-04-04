using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TeamServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly TeamService _service;
    private readonly RoleAssignmentService _roleAssignmentService;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public TeamServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _roleAssignmentService = new RoleAssignmentService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<ISystemTeamSync>(),
            _clock,
            _cache,
            NullLogger<RoleAssignmentService>.Instance);
        var shiftManagementService = new ShiftManagementService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            _cache,
            _clock,
            NullLogger<ShiftManagementService>.Instance);
        _service = new TeamService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<INotificationService>(),
            _roleAssignmentService,
            shiftManagementService,
            Substitute.For<ISystemTeamSync>(),
            _clock,
            _cache,
            NullLogger<TeamService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // IsUserAdminAsync
    // ==========================================================================

    [Fact]
    public async Task IsUserAdminAsync_ActiveAdminRole_ReturnsTrue()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserAdminAsync_NoRoleAssignment_ReturnsFalse()
    {
        var user = SeedUser();
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserAdminAsync_ExpiredRole_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            _clock.GetCurrentInstant() - Duration.FromDays(30),
            _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserAdminAsync_FutureRole_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            _clock.GetCurrentInstant() + Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserAdminAsync_BoardRoleOnly_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // IsUserBoardMemberAsync
    // ==========================================================================

    [Fact]
    public async Task IsUserBoardMemberAsync_ActiveBoardRole_ReturnsTrue()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserBoardMemberAsync_NoRoleAssignment_ReturnsFalse()
    {
        var user = SeedUser();
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserBoardMemberAsync_ExpiredRole_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(30),
            _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserBoardMemberAsync_AdminRoleOnly_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // IsUserCoordinatorOfTeamAsync
    // ==========================================================================

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_ActiveCoordinator_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_MemberNotCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_LeftTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_CoordinatorOfDifferentTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(teamB.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_CoordinatorOfParentTeam_ReturnsTrueForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_MemberOfParentTeam_NotCoordinator_ReturnsFalseForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsTrue_ForOwnSubTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        var member = SeedTeamMember(child.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(child.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsFalse_ForSiblingSubTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var childA = SeedTeam("SubTeamA");
        childA.ParentTeamId = parent.Id;
        var childB = SeedTeam("SubTeamB");
        childB.ParentTeamId = parent.Id;
        var member = SeedTeamMember(childA.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(childA.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(childB.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsFalse_ForParentDepartment()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        var member = SeedTeamMember(child.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(child.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(parent.Id, user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // CanUserApproveRequestsForTeamAsync
    // ==========================================================================

    [Fact]
    public async Task CanUserApproveRequestsForTeamAsync_Admin_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserApproveRequestsForTeamAsync_BoardMember_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedRoleAssignment(user.Id, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserApproveRequestsForTeamAsync_CoordinatorOfTeam_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserApproveRequestsForTeamAsync_CoordinatorOfParentTeam_ReturnsTrueForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(child.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserApproveRequestsForTeamAsync_RegularMember_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserApproveRequestsForTeamAsync_NoRelation_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // IsUserMemberOfTeamAsync
    // ==========================================================================

    [Fact]
    public async Task IsUserMemberOfTeamAsync_ActiveMember_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserMemberOfTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserMemberOfTeamAsync_LeftTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserMemberOfTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserMemberOfTeamAsync_NotMember_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserMemberOfTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // GetTeamBySlugAsync
    // ==========================================================================

    [Fact]
    public async Task GetTeamBySlugAsync_ExistingSlug_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("alpha");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Alpha");
        result.Members.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTeamBySlugAsync_NonExistentSlug_ReturnsNull()
    {
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("non-existent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetTeamBySlugAsync_IncludesUserNavigation()
    {
        var user = SeedUser(displayName: "Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("alpha");

        result!.Members.Single().User.Should().NotBeNull();
        result.Members.Single().User.DisplayName.Should().Be("Alice");
    }

    // ==========================================================================
    // GetTeamByIdAsync
    // ==========================================================================

    [Fact]
    public async Task GetTeamByIdAsync_ExistingId_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamByIdAsync(team.Id);

        result.Should().NotBeNull();
        result!.Members.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTeamByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _service.GetTeamByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetAllTeamsAsync
    // ==========================================================================

    [Fact]
    public async Task GetAllTeamsAsync_ReturnsOnlyActiveTeams()
    {
        SeedTeam("Active");
        SeedTeam("Inactive", isActive: false);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAllTeamsAsync();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Active");
    }

    [Fact]
    public async Task GetAllTeamsAsync_OrderedByName()
    {
        SeedTeam("Charlie");
        SeedTeam("Alpha");
        SeedTeam("Bravo");
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAllTeamsAsync();

        result.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "Alpha", "Bravo", "Charlie" },
            cfg => cfg.WithStrictOrdering());
    }

    [Fact]
    public async Task GetAllTeamsAsync_IncludesOnlyActiveMembers()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        var left = SeedUser(displayName: "Left");
        SeedTeamMember(team.Id, active.Id);
        SeedTeamMember(team.Id, left.Id,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAllTeamsAsync();

        result.Single().Members.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAllTeamsAsync_NoTeams_ReturnsEmpty()
    {
        var result = await _service.GetAllTeamsAsync();

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetUserCreatedTeamsAsync
    // ==========================================================================

    [Fact]
    public async Task GetUserCreatedTeamsAsync_ExcludesSystemTeams()
    {
        SeedTeam("User Team");
        SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserCreatedTeamsAsync();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("User Team");
    }

    [Fact]
    public async Task GetUserCreatedTeamsAsync_ExcludesInactiveTeams()
    {
        SeedTeam("Active");
        SeedTeam("Inactive", isActive: false);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserCreatedTeamsAsync();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Active");
    }

    [Fact]
    public async Task GetUserCreatedTeamsAsync_OrderedByName()
    {
        SeedTeam("Zebra");
        SeedTeam("Apple");
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserCreatedTeamsAsync();

        result[0].Name.Should().Be("Apple");
        result[1].Name.Should().Be("Zebra");
    }

    // ==========================================================================
    // GetUserTeamsAsync
    // ==========================================================================

    [Fact]
    public async Task GetUserTeamsAsync_ReturnsActiveMembershipsOnly()
    {
        var user = SeedUser();
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, user.Id);
        SeedTeamMember(teamB.Id, user.Id,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Should().ContainSingle();
        result[0].TeamId.Should().Be(teamA.Id);
    }

    [Fact]
    public async Task GetUserTeamsAsync_IncludesTeamNavigation()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Single().Team.Should().NotBeNull();
        result.Single().Team.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task GetUserTeamsAsync_NoMemberships_ReturnsEmpty()
    {
        var user = SeedUser();
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetMyTeamMembershipsAsync
    // ==========================================================================

    [Fact]
    public async Task GetMyTeamMembershipsAsync_Coordinator_GetsPendingCountsForManageableNonSystemTeams()
    {
        var user = SeedUser(displayName: "Coordinator");
        var managedTeam = SeedTeam("Alpha", requiresApproval: true);
        var systemTeam = SeedTeam("Volunteers", type: SystemTeamType.Volunteers, requiresApproval: true);
        SeedTeamMember(managedTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(systemTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(managedTeam.Id, SeedUser(displayName: "Requester A").Id);
        SeedJoinRequest(systemTeam.Id, SeedUser(displayName: "Requester B").Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

        result.Should().HaveCount(2);
        result.Single(m => m.TeamId == managedTeam.Id).PendingRequestCount.Should().Be(1);
        result.Single(m => m.TeamId == managedTeam.Id).CanLeave.Should().BeTrue();
        result.Single(m => m.TeamId == systemTeam.Id).PendingRequestCount.Should().Be(0);
        result.Single(m => m.TeamId == systemTeam.Id).CanLeave.Should().BeFalse();
    }

    [Fact]
    public async Task GetMyTeamMembershipsAsync_BoardMember_GetsPendingCountsForRegularMemberships()
    {
        var user = SeedUser(displayName: "Board Human");
        SeedRoleAssignment(
            user.Id,
            RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        SeedJoinRequest(team.Id, SeedUser(displayName: "Requester").Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

        result.Should().ContainSingle();
        result[0].Role.Should().Be(TeamMemberRole.Member);
        result[0].PendingRequestCount.Should().Be(1);
    }

    // ==========================================================================
    // GetTeamDetailAsync
    // ==========================================================================

    [Fact]
    public async Task GetTeamDetailAsync_AnonymousViewer_OnlySeesPublicTeamCoordinatorsAndPublicChildren()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var member = SeedUser(displayName: "Member");
        var team = SeedTeam("Alpha");
        team.IsPublicPage = true;
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(team.Id, member.Id, TeamMemberRole.Member);

        var publicChild = SeedTeam("Public Child");
        publicChild.ParentTeamId = team.Id;
        publicChild.IsPublicPage = true;

        var privateChild = SeedTeam("Private Child");
        privateChild.ParentTeamId = team.Id;
        privateChild.IsPublicPage = false;

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamDetailAsync(team.Slug, userId: null);

        result.Should().NotBeNull();
        result!.IsAuthenticated.Should().BeFalse();
        result.Members.Select(m => m.DisplayName).Should().BeEquivalentTo(
            ["Coordinator"],
            cfg => cfg.WithStrictOrdering());
        result.ChildTeams.Select(t => t.Name).Should().BeEquivalentTo(
            ["Public Child"],
            cfg => cfg.WithStrictOrdering());
        result.CanCurrentUserManage.Should().BeFalse();
        result.RoleDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTeamDetailAsync_AuthenticatedCoordinator_ReturnsViewerStateMembersAndPendingCount()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var member = SeedUser(displayName: "Member");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(team.Id, member.Id, TeamMemberRole.Member);
        var pendingRequest = SeedJoinRequest(team.Id, requester.Id);
        var roleDefinition = SeedTeamRoleDefinition(team.Id, isManagement: true);

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamDetailAsync(team.Slug, coordinator.Id);

        result.Should().NotBeNull();
        result!.IsAuthenticated.Should().BeTrue();
        result.IsCurrentUserMember.Should().BeTrue();
        result.IsCurrentUserCoordinator.Should().BeTrue();
        result.CanCurrentUserManage.Should().BeTrue();
        result.CanCurrentUserEditTeam.Should().BeFalse();
        result.CanCurrentUserJoin.Should().BeFalse();
        result.CanCurrentUserLeave.Should().BeTrue();
        result.PendingRequestCount.Should().Be(1);
        result.CurrentUserPendingRequestId.Should().BeNull();
        result.Members.Select(m => (m.DisplayName, m.Role)).Should().BeEquivalentTo(
            [("Member", TeamMemberRole.Member), ("Coordinator", TeamMemberRole.Coordinator)],
            cfg => cfg.WithStrictOrdering());
        result.RoleDefinitions.Select(d => d.Id).Should().ContainSingle().Which.Should().Be(roleDefinition.Id);
        pendingRequest.Status.Should().Be(TeamJoinRequestStatus.Pending);
    }

    // ==========================================================================
    // GetPendingRequestsForApproverAsync
    // ==========================================================================

    [Fact]
    public async Task GetPendingRequestsForApproverAsync_BoardMember_ReturnsAllPending()
    {
        var approver = SeedUser();
        SeedRoleAssignment(approver.Id, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        var teamA = SeedTeam("Alpha", requiresApproval: true);
        var teamB = SeedTeam("Beta", requiresApproval: true);
        var requestor1 = SeedUser(displayName: "R1");
        var requestor2 = SeedUser(displayName: "R2");
        SeedJoinRequest(teamA.Id, requestor1.Id);
        SeedJoinRequest(teamB.Id, requestor2.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForApproverAsync(approver.Id);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetPendingRequestsForApproverAsync_Coordinator_ReturnsOnlyOwnTeamRequests()
    {
        var coordinator = SeedUser();
        var teamA = SeedTeam("Alpha", requiresApproval: true);
        var teamB = SeedTeam("Beta", requiresApproval: true);
        SeedTeamMember(teamA.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var requestor1 = SeedUser(displayName: "R1");
        var requestor2 = SeedUser(displayName: "R2");
        SeedJoinRequest(teamA.Id, requestor1.Id);
        SeedJoinRequest(teamB.Id, requestor2.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForApproverAsync(coordinator.Id);

        result.Should().ContainSingle();
        result[0].TeamId.Should().Be(teamA.Id);
    }

    [Fact]
    public async Task GetPendingRequestsForApproverAsync_CoordinatorOfParent_IncludesChildTeamRequests()
    {
        var coordinator = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var requestor = SeedUser(displayName: "R1");
        SeedJoinRequest(child.Id, requestor.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForApproverAsync(coordinator.Id);

        result.Should().ContainSingle();
        result[0].TeamId.Should().Be(child.Id);
    }

    [Fact]
    public async Task GetPendingRequestsForApproverAsync_RegularUser_ReturnsEmpty()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        var requestor = SeedUser(displayName: "R1");
        SeedJoinRequest(team.Id, requestor.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForApproverAsync(user.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingRequestsForApproverAsync_ExcludesNonPendingRequests()
    {
        var approver = SeedUser();
        SeedRoleAssignment(approver.Id, RoleNames.Board,
            _clock.GetCurrentInstant() - Duration.FromDays(10));
        var team = SeedTeam("Alpha", requiresApproval: true);
        var r1 = SeedUser(displayName: "R1");
        var r2 = SeedUser(displayName: "R2");
        SeedJoinRequest(team.Id, r1.Id, TeamJoinRequestStatus.Approved);
        SeedJoinRequest(team.Id, r2.Id, TeamJoinRequestStatus.Pending);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForApproverAsync(approver.Id);

        result.Should().ContainSingle();
    }

    // ==========================================================================
    // GetPendingRequestsForTeamAsync
    // ==========================================================================

    [Fact]
    public async Task GetPendingRequestsForTeamAsync_ReturnsPendingOnly()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        SeedJoinRequest(team.Id, u1.Id);
        SeedJoinRequest(team.Id, u2.Id, TeamJoinRequestStatus.Rejected);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id);

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(u1.Id);
    }

    [Fact]
    public async Task GetPendingRequestsForTeamAsync_NoRequests_ReturnsEmpty()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingRequestsForTeamAsync_OrderedByRequestedAt()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "First");
        var u2 = SeedUser(displayName: "Second");
        // Seed first request earlier
        var earlier = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = u1.Id,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = _clock.GetCurrentInstant() - Duration.FromHours(2)
        };
        _dbContext.TeamJoinRequests.Add(earlier);
        var later = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = u2.Id,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = _clock.GetCurrentInstant() - Duration.FromHours(1)
        };
        _dbContext.TeamJoinRequests.Add(later);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id);

        result[0].UserId.Should().Be(u1.Id);
        result[1].UserId.Should().Be(u2.Id);
    }

    // ==========================================================================
    // GetUserPendingRequestAsync
    // ==========================================================================

    [Fact]
    public async Task GetUserPendingRequestAsync_HasPending_ReturnsRequest()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedJoinRequest(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task GetUserPendingRequestAsync_NoRequest_ReturnsNull()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUserPendingRequestAsync_ApprovedRequest_ReturnsNull()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedJoinRequest(team.Id, user.Id, TeamJoinRequestStatus.Approved);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id);

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetTeamMembersAsync
    // ==========================================================================

    [Fact]
    public async Task GetTeamMembersAsync_ReturnsOnlyActiveMembers()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        var left = SeedUser(displayName: "Left");
        SeedTeamMember(team.Id, active.Id);
        SeedTeamMember(team.Id, left.Id,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamMembersAsync(team.Id);

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(active.Id);
    }

    [Fact]
    public async Task GetTeamMembersAsync_OrderedByRoleThenJoinedAt()
    {
        var team = SeedTeam("Alpha");
        var coordinator = SeedUser(displayName: "Coordinator");
        var memberEarly = SeedUser(displayName: "Early");
        var memberLate = SeedUser(displayName: "Late");
        // Coordinator role (enum value 1 > Member 0) — OrderBy(Role) ascending means Member(0) first, Coordinator(1) second
        var m1 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = coordinator.Id,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = _clock.GetCurrentInstant() - Duration.FromDays(5)
        };
        var m2 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberEarly.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant() - Duration.FromDays(3)
        };
        var m3 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberLate.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant() - Duration.FromDays(1)
        };
        await _dbContext.TeamMembers.AddRangeAsync(m1, m2, m3);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamMembersAsync(team.Id);

        result.Should().HaveCount(3);
        // Ascending by Role: Member(0) before Coordinator(1)
        result[0].Role.Should().Be(TeamMemberRole.Member);
        result[1].Role.Should().Be(TeamMemberRole.Member);
        result[2].Role.Should().Be(TeamMemberRole.Coordinator);
        // Members ordered by JoinedAt ascending
        result[0].UserId.Should().Be(memberEarly.Id);
        result[1].UserId.Should().Be(memberLate.Id);
    }

    [Fact]
    public async Task GetTeamMembersAsync_IncludesUserNavigation()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser(displayName: "Alice");
        SeedTeamMember(team.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamMembersAsync(team.Id);

        result.Single().User.Should().NotBeNull();
        result.Single().User.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task GetTeamMembersAsync_NoMembers_ReturnsEmpty()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamMembersAsync(team.Id);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetPendingRequestCountsByTeamIdsAsync
    // ==========================================================================

    [Fact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_ReturnsCounts()
    {
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        var u3 = SeedUser(displayName: "U3");
        SeedJoinRequest(teamA.Id, u1.Id);
        SeedJoinRequest(teamA.Id, u2.Id);
        SeedJoinRequest(teamB.Id, u3.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([teamA.Id, teamB.Id]);

        result[teamA.Id].Should().Be(2);
        result[teamB.Id].Should().Be(1);
    }

    [Fact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_ExcludesNonPending()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        SeedJoinRequest(team.Id, u1.Id);
        SeedJoinRequest(team.Id, u2.Id, TeamJoinRequestStatus.Approved);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([team.Id]);

        result[team.Id].Should().Be(1);
    }

    [Fact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_TeamWithNoPending_ReturnsZero()
    {
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([team.Id]);

        result[team.Id].Should().Be(0);
    }

    // ==========================================================================
    // GetNonSystemTeamNamesByUserIdsAsync
    // ==========================================================================

    [Fact]
    public async Task GetNonSystemTeamNamesByUserIdsAsync_ReturnsTeamNamesGroupedByUser()
    {
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, u1.Id);
        SeedTeamMember(teamB.Id, u1.Id);
        SeedTeamMember(teamA.Id, u2.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetNonSystemTeamNamesByUserIdsAsync([u1.Id, u2.Id]);

        result[u1.Id].Should().HaveCount(2);
        result[u1.Id].Should().Contain("Alpha");
        result[u1.Id].Should().Contain("Beta");
        result[u2.Id].Should().ContainSingle("Alpha");
    }

    [Fact]
    public async Task GetNonSystemTeamNamesByUserIdsAsync_ExcludesSystemTeams()
    {
        var user = SeedUser();
        var userTeam = SeedTeam("User Team");
        var sysTeam = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        SeedTeamMember(userTeam.Id, user.Id);
        SeedTeamMember(sysTeam.Id, user.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetNonSystemTeamNamesByUserIdsAsync([user.Id]);

        result[user.Id].Should().ContainSingle("User Team");
    }

    [Fact]
    public async Task GetNonSystemTeamNamesByUserIdsAsync_ExcludesLeftMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id,
            leftAt: _clock.GetCurrentInstant() - Duration.FromDays(1));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetNonSystemTeamNamesByUserIdsAsync([user.Id]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNonSystemTeamNamesByUserIdsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var result = await _service.GetNonSystemTeamNamesByUserIdsAsync([]);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetAllTeamsForAdminAsync
    // ==========================================================================

    [Fact]
    public async Task GetAllTeamsForAdminAsync_ReturnsPaginatedResults()
    {
        SeedTeam("Alpha");
        SeedTeam("Beta");
        SeedTeam("Charlie");
        await _dbContext.SaveChangesAsync();

        var (items, totalCount) = await _service.GetAllTeamsForAdminAsync(1, 2);

        items.Should().HaveCount(2);
        totalCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAllTeamsForAdminAsync_SecondPage_ReturnsRemainingItems()
    {
        SeedTeam("Alpha");
        SeedTeam("Beta");
        SeedTeam("Charlie");
        await _dbContext.SaveChangesAsync();

        var (items, totalCount) = await _service.GetAllTeamsForAdminAsync(2, 2);

        items.Should().ContainSingle();
        totalCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAllTeamsForAdminAsync_IncludesMembers()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        SeedTeamMember(team.Id, active.Id);
        await _dbContext.SaveChangesAsync();

        var (items, _) = await _service.GetAllTeamsForAdminAsync(1, 10);

        items.Single().Members.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAllTeamsForAdminAsync_IncludesJoinRequests()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        SeedJoinRequest(team.Id, u1.Id);
        await _dbContext.SaveChangesAsync();

        var (items, _) = await _service.GetAllTeamsForAdminAsync(1, 10);

        items.Single().JoinRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAllTeamsForAdminAsync_IncludesInactiveTeams()
    {
        SeedTeam("Active");
        SeedTeam("Inactive", isActive: false);
        await _dbContext.SaveChangesAsync();

        var (items, totalCount) = await _service.GetAllTeamsForAdminAsync(1, 10);

        totalCount.Should().Be(2);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllTeamsForAdminAsync_SystemTeamsOrderedFirst()
    {
        SeedTeam("Zebra");
        SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await _dbContext.SaveChangesAsync();

        var (items, _) = await _service.GetAllTeamsForAdminAsync(1, 10);

        // SystemTeamType.None(0) < Volunteers(1), so None sorts first in ascending order
        items[0].SystemTeamType.Should().Be(SystemTeamType.None);
        items[1].SystemTeamType.Should().Be(SystemTeamType.Volunteers);
    }

    // ==========================================================================
    // AddMemberToTeamAsync
    // ==========================================================================

    [Fact]
    public async Task AddMemberToTeamAsync_ValidUser_CreatesMembership()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        await _dbContext.SaveChangesAsync();

        var result = await _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id);

        result.Should().NotBeNull();
        result.TeamId.Should().Be(team.Id);
        result.UserId.Should().Be(target.Id);
        result.Role.Should().Be(TeamMemberRole.Member);
        result.LeftAt.Should().BeNull();

        var memberInDb = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == target.Id && tm.LeftAt == null);
        memberInDb.Should().NotBeNull();
    }

    [Fact]
    public async Task AddMemberToTeamAsync_AlreadyMember_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, target.Id);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already a member*");
    }

    [Fact]
    public async Task AddMemberToTeamAsync_SystemTeam_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [Fact]
    public async Task LeaveTeamAsync_RemovesManagementAssignments_InvalidatesShiftAuthorizationCache()
    {
        var user = SeedUser();
        var team = SeedTeam("Operations");
        var roleDefinition = SeedTeamRoleDefinition(team.Id, isManagement: true);
        var member = SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamRoleAssignment(roleDefinition.Id, member.Id);
        await _dbContext.SaveChangesAsync();
        _cache.Set(CacheKeys.ShiftAuthorization(user.Id), new[] { team.Id });

        var result = await _service.LeaveTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
        _cache.TryGetValue(CacheKeys.ShiftAuthorization(user.Id), out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetRosterAsync_ExpandsSlotsAndSortsByPriorityThenName()
    {
        var alphaTeam = SeedTeam("Alpha");
        var betaTeam = SeedTeam("Beta");
        var alphaMember = SeedUser(displayName: "Assigned Human");
        var alphaTeamMember = SeedTeamMember(alphaTeam.Id, alphaMember.Id);

        var alphaDefinition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = alphaTeam.Id,
            Team = alphaTeam,
            Name = "Lead",
            SlotCount = 2,
            Priorities = [SlotPriority.Important, SlotPriority.None],
            SortOrder = 0,
            Period = RolePeriod.YearRound,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        alphaDefinition.Assignments.Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = alphaDefinition.Id,
            TeamMemberId = alphaTeamMember.Id,
            TeamMember = alphaTeamMember,
            SlotIndex = 0,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        });

        var betaDefinition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = betaTeam.Id,
            Team = betaTeam,
            Name = "Greeter",
            SlotCount = 1,
            Priorities = [SlotPriority.Critical],
            SortOrder = 0,
            Period = RolePeriod.Event,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        await _dbContext.TeamRoleDefinitions.AddRangeAsync(alphaDefinition, betaDefinition);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterAsync(priority: null, status: null, period: null);

        result.Select(slot => (slot.TeamName, slot.RoleName, slot.SlotNumber))
            .Should()
            .ContainInOrder(
                ("Beta", "Greeter", 1),
                ("Alpha", "Lead", 1),
                ("Alpha", "Lead", 2));

        result[0].Priority.Should().Be(nameof(SlotPriority.Critical));
        result[0].PriorityBadgeClass.Should().Be("bg-danger");
        result[1].AssignedUserName.Should().Be("Assigned Human");
        result[2].Priority.Should().Be(nameof(SlotPriority.None));
        result[2].PriorityBadgeClass.Should().Be("bg-light text-dark");
    }

    [Fact]
    public async Task GetRosterAsync_AppliesPriorityStatusAndPeriodFilters()
    {
        var team = SeedTeam("Alpha");
        var assignedUser = SeedUser(displayName: "Assigned Human");
        var assignedTeamMember = SeedTeamMember(team.Id, assignedUser.Id);

        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Team = team,
            Name = "Lead",
            SlotCount = 3,
            Priorities = [SlotPriority.Critical, SlotPriority.Important, SlotPriority.Important],
            SortOrder = 0,
            Period = RolePeriod.Event,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        definition.Assignments.Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = definition.Id,
            TeamMemberId = assignedTeamMember.Id,
            TeamMember = assignedTeamMember,
            SlotIndex = 1,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        });

        _dbContext.TeamRoleDefinitions.Add(definition);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterAsync(
            priority: nameof(SlotPriority.Important),
            status: "open",
            period: nameof(RolePeriod.Event));

        result.Should().ContainSingle();
        result[0].SlotNumber.Should().Be(3);
        result[0].Priority.Should().Be(nameof(SlotPriority.Important));
        result[0].Period.Should().Be(nameof(RolePeriod.Event));
        result[0].IsFilled.Should().BeFalse();
    }

    // ==========================================================================
    // GetAdminTeamListAsync — PendingShiftSignupCount
    // ==========================================================================

    [Fact]
    public async Task GetAdminTeamListAsync_CountsPendingShiftSignupsForActiveEvent()
    {
        var team = SeedTeam("Dept A");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id);

        var activeEvent = SeedEventSettings("Active Event", isActive: true);
        var inactiveEvent = SeedEventSettings("Old Event", isActive: false);

        // Rota on active event with 2 pending signups
        var activeRota = SeedRota(team.Id, activeEvent.Id, "Gate Shifts");
        var shift1 = SeedShift(activeRota.Id);
        SeedShiftSignup(shift1.Id, user.Id, SignupStatus.Pending);
        SeedShiftSignup(shift1.Id, Guid.NewGuid(), SignupStatus.Pending);
        SeedShiftSignup(shift1.Id, Guid.NewGuid(), SignupStatus.Confirmed); // not pending

        // Rota on inactive event — should NOT be counted
        var oldRota = SeedRota(team.Id, inactiveEvent.Id, "Old Shifts");
        var oldShift = SeedShift(oldRota.Id);
        SeedShiftSignup(oldShift.Id, user.Id, SignupStatus.Pending);

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 500);

        var summary = result.Teams.Should().ContainSingle(t => t.Name == "Dept A").Subject;
        summary.PendingShiftSignupCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAdminTeamListAsync_ReturnsZeroPendingShifts_WhenNoActiveEvent()
    {
        var team = SeedTeam("Dept B");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id);

        var inactiveEvent = SeedEventSettings("Past Event", isActive: false);
        var rota = SeedRota(team.Id, inactiveEvent.Id, "Shifts");
        var shift = SeedShift(rota.Id);
        SeedShiftSignup(shift.Id, user.Id, SignupStatus.Pending);

        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 500);

        var summary = result.Teams.Should().ContainSingle(t => t.Name == "Dept B").Subject;
        summary.PendingShiftSignupCount.Should().Be(0);
    }

    // --- Helpers ---

    private User SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var userId = id ?? Guid.NewGuid();
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

    private Team SeedTeam(string name, SystemTeamType type = SystemTeamType.None, Guid? id = null, bool isActive = true, bool requiresApproval = false)
    {
        var team = new Team
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            SystemTeamType = type,
            IsActive = isActive,
            RequiresApproval = requiresApproval,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private TeamMember SeedTeamMember(Guid teamId, Guid userId, TeamMemberRole role = TeamMemberRole.Member, Instant? leftAt = null)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Team = _dbContext.Teams.Local.Single(t => t.Id == teamId),
            UserId = userId,
            User = _dbContext.Users.Local.Single(u => u.Id == userId),
            Role = role,
            JoinedAt = _clock.GetCurrentInstant(),
            LeftAt = leftAt
        };
        _dbContext.TeamMembers.Add(member);
        return member;
    }

    private RoleAssignment SeedRoleAssignment(Guid userId, string roleName, Instant validFrom, Instant? validTo = null)
    {
        var ra = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = Guid.NewGuid()
        };
        _dbContext.RoleAssignments.Add(ra);
        return ra;
    }

    private TeamJoinRequest SeedJoinRequest(Guid teamId, Guid userId, TeamJoinRequestStatus status = TeamJoinRequestStatus.Pending)
    {
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Status = status,
            RequestedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamJoinRequests.Add(request);
        return request;
    }

    private TeamRoleDefinition SeedTeamRoleDefinition(Guid teamId, bool isManagement)
    {
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = isManagement ? "Coordinator" : "Member Role",
            IsManagement = isManagement,
            SlotCount = 1,
            SortOrder = 0,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamRoleDefinitions.Add(definition);
        return definition;
    }

    private TeamRoleAssignment SeedTeamRoleAssignment(Guid roleDefinitionId, Guid teamMemberId)
    {
        var assignment = new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = roleDefinitionId,
            TeamMemberId = teamMemberId,
            SlotIndex = 0,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        };
        _dbContext.TeamRoleAssignments.Add(assignment);
        return assignment;
    }

    private EventSettings SeedEventSettings(string name, bool isActive)
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = name,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -7,
            EventEndOffset = 5,
            StrikeEndOffset = 7,
            IsActive = isActive,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.EventSettings.Add(es);
        return es;
    }

    private Rota SeedRota(Guid teamId, Guid eventSettingsId, string name)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            EventSettingsId = eventSettingsId,
            Name = name,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Rotas.Add(rota);
        return rota;
    }

    private Shift SeedShift(Guid rotaId)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rotaId,
            DayOffset = 0,
            StartTime = new LocalTime(9, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Shifts.Add(shift);
        return shift;
    }

    private ShiftSignup SeedShiftSignup(Guid shiftId, Guid userId, SignupStatus status)
    {
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            UserId = userId,
            Status = status,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.ShiftSignups.Add(signup);
        return signup;
    }
}
