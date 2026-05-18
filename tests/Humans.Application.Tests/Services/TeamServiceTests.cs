// Tests seed TeamMember.User navs directly for DB-roundtrip verification.
// TeamMember.User is Obsolete per §6c; the production path populates it
// in-memory via TeamService, but the tests set it on the entity before
// SaveChanges. File-wide disable cleared when tests switch to seeding via
// service calls instead of raw entity inserts.
#pragma warning disable CS0618
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Repositories.Teams;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;
using TeamService = Humans.Application.Services.Teams.TeamService;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Auth;
using Humans.Infrastructure.Repositories.Auth;
using Humans.Infrastructure.Repositories.Shifts;

namespace Humans.Application.Tests.Services;

public sealed class TeamServiceTests : ServiceTestHarness
{
    private readonly TeamService _service;
    private readonly RoleAssignmentService _roleAssignmentService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IShiftAuthorizationInvalidator _shiftAuthInvalidator;

    public TeamServiceTests()
    {
        _roleAssignmentService = new RoleAssignmentService(
            new RoleAssignmentRepository(DbFactory),
            Substitute.For<IUserService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationEmitter>(),
            Substitute.For<ISystemTeamSync>(),
            Substitute.For<INavBadgeCacheInvalidator>(),
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            Substitute.For<IRoleAssignmentCacheInvalidator>(),
            Clock,
            NullLogger<RoleAssignmentService>.Instance);
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(Substitute.For<ITeamService>());
        serviceProvider.GetService(typeof(IRoleAssignmentService)).Returns(_roleAssignmentService);
        serviceProvider.GetService(typeof(IEmailService)).Returns(Substitute.For<IEmailService>());
        serviceProvider.GetService(typeof(ISystemTeamSync)).Returns(Substitute.For<ISystemTeamSync>());
        _teamResourceService = Substitute.For<ITeamResourceService>();
        _teamResourceService
            .GetTeamResourceSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamResourceSummary>());
        serviceProvider.GetService(typeof(ITeamResourceService)).Returns(_teamResourceService);
        var shiftManagementService = new ShiftManagementService(
            new ShiftManagementRepository(DbFactory),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IAdminAuthorizationService>(),
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        // Shift-auth invalidation: production path uses IShiftAuthorizationInvalidator
        // (backed by ShiftManagementService in DI). In tests we redirect it to the
        // same IMemoryCache entry so legacy assertions on the ShiftAuthorization
        // cache key keep working.
        _shiftAuthInvalidator = Substitute.For<IShiftAuthorizationInvalidator>();
        _shiftAuthInvalidator
            .When(s => s.Invalidate(Arg.Any<Guid>()))
            .Do(ci => Cache.Remove(CacheKeys.ShiftAuthorization(ci.Arg<Guid>())));

        // Build the user service before wiring it into the locator — NSubstitute
        // can't attach an outer .Returns() to a factory that itself configures
        // substitute calls.
        var userService = NewDbBackedUserService();
        serviceProvider.GetService(typeof(IUserService)).Returns(userService);
        _service = new TeamService(
            new TeamRepository(DbFactory),
            Substitute.For<IAuditLogService>(),
            Substitute.For<INotificationEmitter>(),
            shiftManagementService,
            Substitute.For<INotificationMeterCacheInvalidator>(),
            _shiftAuthInvalidator,
            Substitute.For<IAdminAuthorizationService>(),
            serviceProvider,
            Clock,
            NullLogger<TeamService>.Instance);
    }

    // ==========================================================================
    // IsUserAdminAsync
    // ==========================================================================

    [HumansFact]
    public async Task IsUserAdminAsync_ActiveAdminRole_ReturnsTrue()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserAdminAsync_NoRoleAssignment_ReturnsFalse()
    {
        var user = SeedUser();
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserAdminAsync_ExpiredRole_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(30),
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserAdminAsync_FutureRole_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() + Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserAdminAsync_BoardRoleOnly_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserAdminAsync(user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // IsUserBoardMemberAsync
    // ==========================================================================

    [HumansFact]
    public async Task IsUserBoardMemberAsync_ActiveBoardRole_ReturnsTrue()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserBoardMemberAsync_NoRoleAssignment_ReturnsFalse()
    {
        var user = SeedUser();
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserBoardMemberAsync_ExpiredRole_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(30),
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserBoardMemberAsync_AdminRoleOnly_ReturnsFalse()
    {
        var user = SeedUser();
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        await Db.SaveChangesAsync();

        var result = await _roleAssignmentService.IsUserBoardMemberAsync(user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // IsUserCoordinatorOfTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_ActiveCoordinator_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_MemberNotCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_LeftTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_CoordinatorOfDifferentTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(teamB.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_CoordinatorOfParentTeam_ReturnsTrueForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_MemberOfParentTeam_NotCoordinator_ReturnsFalseForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsTrue_ForOwnSubTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        var member = SeedTeamMember(child.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(child.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
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
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(childB.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsFalse_ForParentDepartment()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        var member = SeedTeamMember(child.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(child.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await Db.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(parent.Id, user.Id);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // CanUserApproveRequestsForTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task CanUserApproveRequestsForTeamAsync_Admin_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedRoleAssignment(user.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        await Db.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserApproveRequestsForTeamAsync_BoardMember_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedRoleAssignment(user.Id, RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        await Db.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserApproveRequestsForTeamAsync_CoordinatorOfTeam_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserApproveRequestsForTeamAsync_CoordinatorOfParentTeam_ReturnsTrueForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(child.Id, user.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserApproveRequestsForTeamAsync_RegularMember_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserApproveRequestsForTeamAsync_NoRelation_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdateTeamPageContentWithResultAsync_NormalizesCallsToActionAndUpdatesPage()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.UpdateTeamPageContentAsync(
            team.Id,
            "Welcome", [
                new TeamPageCallToActionInput(" Join ", " /join ", CallToActionStyle.Primary),
                new TeamPageCallToActionInput("", "/ignored", CallToActionStyle.Secondary)
            ],
            isPublicPage: true,
            showCoordinatorsOnPublicPage: true,
            user.Id);

        result.Succeeded.Should().BeTrue();

        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.PageContent.Should().Be("Welcome");
        stored.CallsToAction.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CallToAction
            {
                Text = "Join",
                Url = "/join",
                Style = CallToActionStyle.Primary
            });
        stored.IsPublicPage.Should().BeTrue();
        stored.ShowCoordinatorsOnPublicPage.Should().BeTrue();
    }

    // ==========================================================================
    // GetTeamBySlugAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamBySlugAsync_ExistingSlug_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("alpha");

        result.Should().NotBeNull();
        result.Name.Should().Be("Alpha");
        result.Members.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetTeamBySlugAsync_NonExistentSlug_ReturnsNull()
    {
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("non-existent");

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamBySlugAsync_IncludesUserNavigation()
    {
        var user = SeedUser(displayName: "Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("alpha");

        result!.Members.Single().User.Should().NotBeNull();
        result.Members.Single().User.DisplayName.Should().Be("Alice");
    }

    // ==========================================================================
    // GetTeamByIdAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamByIdAsync_ExistingId_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamByIdAsync(team.Id);

        result.Should().NotBeNull();
        result.Members.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetTeamByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _service.GetTeamByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetAllTeamsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetAllTeamsAsync_ReturnsOnlyActiveTeams()
    {
        SeedTeam("Active");
        SeedTeam("Inactive", isActive: false);
        await Db.SaveChangesAsync();

        var result = await _service.GetAllTeamsAsync();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Active");
    }

    [HumansFact]
    public async Task GetAllTeamsAsync_ReturnsActiveTeamsWithoutPresentationOrdering()
    {
        SeedTeam("Charlie");
        SeedTeam("Alpha");
        SeedTeam("Bravo");
        await Db.SaveChangesAsync();

        var result = await _service.GetAllTeamsAsync();

        result.Select(t => t.Name).Should().BeEquivalentTo("Alpha", "Bravo", "Charlie");
    }

    [HumansFact]
    public async Task GetAllTeamsAsync_IncludesOnlyActiveMembers()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        var left = SeedUser(displayName: "Left");
        SeedTeamMember(team.Id, active.Id);
        SeedTeamMember(team.Id, left.Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.GetAllTeamsAsync();

        result.Single().Members.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetAllTeamsAsync_NoTeams_ReturnsEmpty()
    {
        var result = await _service.GetAllTeamsAsync();

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetUserTeamsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetUserTeamsAsync_ReturnsActiveMembershipsOnly()
    {
        var user = SeedUser();
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, user.Id);
        SeedTeamMember(teamB.Id, user.Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Should().ContainSingle();
        result[0].TeamId.Should().Be(teamA.Id);
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_IncludesTeamNavigation()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Single().Team.Should().NotBeNull();
        result.Single().Team.Name.Should().Be("Alpha");
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_NoMemberships_ReturnsEmpty()
    {
        var user = SeedUser();
        await Db.SaveChangesAsync();

        var result = await _service.GetUserTeamsAsync(user.Id);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetMyTeamMembershipsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_Coordinator_GetsPendingCountsForManageableNonSystemTeams()
    {
        var user = SeedUser(displayName: "Coordinator");
        var managedTeam = SeedTeam("Alpha", requiresApproval: true);
        var systemTeam = SeedTeam("Volunteers", type: SystemTeamType.Volunteers, requiresApproval: true);
        SeedTeamMember(managedTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(systemTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(managedTeam.Id, SeedUser(displayName: "Requester A").Id);
        SeedJoinRequest(systemTeam.Id, SeedUser(displayName: "Requester B").Id);
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
        var user = SeedUser(displayName: "Board Human");
        SeedRoleAssignment(
            user.Id,
            RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        SeedJoinRequest(team.Id, SeedUser(displayName: "Requester").Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetMyTeamMembershipsAsync(user.Id);

        result.Should().ContainSingle();
        result[0].Role.Should().Be(TeamMemberRole.Member);
        result[0].PendingRequestCount.Should().Be(1);
    }

    // ==========================================================================
    // GetTeamDetailAsync
    // ==========================================================================

    [HumansFact]
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

        await Db.SaveChangesAsync();

        var result = await _service.GetTeamDetailAsync(team.Slug, userId: null);

        result.Should().NotBeNull();
        result.IsAuthenticated.Should().BeFalse();
        result.Members.Select(m => m.DisplayName).Should().BeEquivalentTo(
            ["Coordinator"],
            cfg => cfg.WithStrictOrdering());
        result.ChildTeams.Select(t => t.Name).Should().BeEquivalentTo(
            ["Public Child"],
            cfg => cfg.WithStrictOrdering());
        result.CanCurrentUserManage.Should().BeFalse();
        result.RoleDefinitions.Should().BeEmpty();
    }

    [HumansFact]
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

        await Db.SaveChangesAsync();

        var result = await _service.GetTeamDetailAsync(team.Slug, coordinator.Id);

        result.Should().NotBeNull();
        result.IsAuthenticated.Should().BeTrue();
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
    // GetPendingRequestsForTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetPendingRequestsForTeamAsync_ReturnsPendingOnly()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        SeedJoinRequest(team.Id, u1.Id);
        SeedJoinRequest(team.Id, u2.Id, TeamJoinRequestStatus.Rejected);
        await Db.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id);

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(u1.Id);
    }

    [HumansFact]
    public async Task GetPendingRequestsForTeamAsync_NoRequests_ReturnsEmpty()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id);

        result.Should().BeEmpty();
    }

    [HumansFact]
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
            RequestedAt = Clock.GetCurrentInstant() - Duration.FromHours(2)
        };
        Db.TeamJoinRequests.Add(earlier);
        var later = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = u2.Id,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = Clock.GetCurrentInstant() - Duration.FromHours(1)
        };
        Db.TeamJoinRequests.Add(later);
        await Db.SaveChangesAsync();

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id);

        result[0].UserId.Should().Be(u1.Id);
        result[1].UserId.Should().Be(u2.Id);
    }

    // ==========================================================================
    // GetUserPendingRequestAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetUserPendingRequestAsync_HasPending_ReturnsRequest()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedJoinRequest(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id);

        result.Should().NotBeNull();
        result.UserId.Should().Be(user.Id);
    }

    [HumansFact]
    public async Task GetUserPendingRequestAsync_NoRequest_ReturnsNull()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        await Db.SaveChangesAsync();

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetUserPendingRequestAsync_ApprovedRequest_ReturnsNull()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedJoinRequest(team.Id, user.Id, TeamJoinRequestStatus.Approved);
        await Db.SaveChangesAsync();

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id);

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamAsync_ReturnsOnlyActiveMembers()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        var left = SeedUser(displayName: "Left");
        SeedTeamMember(team.Id, active.Id);
        SeedTeamMember(team.Id, left.Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        result.Should().NotBeNull();
        result.Members.Should().ContainSingle();
        result.Members[0].UserId.Should().Be(active.Id);
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesMemberRoleAndJoinedAt()
    {
        var team = SeedTeam("Alpha");
        var coordinator = SeedUser(displayName: "Coordinator");
        var memberEarly = SeedUser(displayName: "Early");
        var memberLate = SeedUser(displayName: "Late");
        // The active team read model preserves membership facts; presentation owns display ordering.
        var m1 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = coordinator.Id,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = Clock.GetCurrentInstant() - Duration.FromDays(5)
        };
        var m2 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberEarly.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = Clock.GetCurrentInstant() - Duration.FromDays(3)
        };
        var m3 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberLate.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = Clock.GetCurrentInstant() - Duration.FromDays(1)
        };
        await Db.TeamMembers.AddRangeAsync(m1, m2, m3);
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        result.Should().NotBeNull();
        result.Members.Should().HaveCount(3);
        result.Members.Count(m => m.Role == TeamMemberRole.Member).Should().Be(2);
        result.Members.Count(m => m.Role == TeamMemberRole.Coordinator).Should().Be(1);
        result.Members.Select(m => m.UserId).Should().Contain([memberEarly.Id, memberLate.Id]);
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesUserSlice()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser(displayName: "Alice");
        SeedTeamMember(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        var member = result!.Members.Should().ContainSingle().Subject;
        member.DisplayName.Should().Be("Alice");
    }

    [HumansFact]
    public async Task GetTeamAsync_NoMembers_ReturnsEmpty()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        result.Should().NotBeNull();
        result.Members.Should().BeEmpty();
    }

    // ==========================================================================
    // GetPendingRequestCountsByTeamIdsAsync
    // ==========================================================================

    [HumansFact]
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
        await Db.SaveChangesAsync();

        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([teamA.Id, teamB.Id]);

        result[teamA.Id].Should().Be(2);
        result[teamB.Id].Should().Be(1);
    }

    [HumansFact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_ExcludesNonPending()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        SeedJoinRequest(team.Id, u1.Id);
        SeedJoinRequest(team.Id, u2.Id, TeamJoinRequestStatus.Approved);
        await Db.SaveChangesAsync();

        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([team.Id]);

        result[team.Id].Should().Be(1);
    }

    [HumansFact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_EmptyInput_ReturnsEmptyDict()
    {
        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([]);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPendingRequestCountsByTeamIdsAsync_TeamWithNoPending_ReturnsZero()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.GetPendingRequestCountsByTeamIdsAsync([team.Id]);

        result[team.Id].Should().Be(0);
    }

    // ==========================================================================
    // GetAdminTeamListAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetAdminTeamListAsync_ReturnsPaginatedResults()
    {
        SeedTeam("Alpha");
        SeedTeam("Beta");
        SeedTeam("Charlie");
        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 2);

        result.Teams.Should().HaveCount(2);
        result.TotalCount.Should().Be(3);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_SecondPage_ReturnsRemainingItems()
    {
        SeedTeam("Alpha");
        SeedTeam("Beta");
        SeedTeam("Charlie");
        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(2, 2);

        result.Teams.Should().ContainSingle();
        result.TotalCount.Should().Be(3);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_IncludesMemberCount()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        SeedTeamMember(team.Id, active.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 10);

        result.Teams.Single().MemberCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_IncludesPendingRequestCount()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        SeedJoinRequest(team.Id, u1.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 10);

        result.Teams.Single().PendingRequestCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_IncludesInactiveTeams()
    {
        SeedTeam("Active");
        SeedTeam("Inactive", isActive: false);
        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 10);

        result.TotalCount.Should().Be(2);
        result.Teams.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_SystemTeamsOrderedFirst()
    {
        SeedTeam("Zebra");
        SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 10);

        // SystemTeamType.None(0) < Volunteers(1), so None sorts first in ascending order
        result.Teams[0].SystemTeamType.Should().BeNull();
        result.Teams[1].SystemTeamType.Should().Be(nameof(SystemTeamType.Volunteers));
    }

    // ==========================================================================
    // AddMemberToTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddMemberToTeamAsync_ValidUser_CreatesMembership()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id);

        result.Should().NotBeNull();
        result.TeamId.Should().Be(team.Id);
        result.UserId.Should().Be(target.Id);
        result.Role.Should().Be(TeamMemberRole.Member);
        result.LeftAt.Should().BeNull();

        var memberInDb = await Db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == target.Id && tm.LeftAt == null);
        memberInDb.Should().NotBeNull();
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_AlreadyMember_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, target.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already a member*");
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_SystemTeam_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await Db.SaveChangesAsync();

        var act = () => _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [HumansFact]
    public async Task LeaveTeamAsync_RemovesManagementAssignments_InvalidatesShiftAuthorizationCache()
    {
        var user = SeedUser();
        var team = SeedTeam("Operations");
        var roleDefinition = SeedTeamRoleDefinition(team.Id, isManagement: true);
        var member = SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamRoleAssignment(roleDefinition.Id, member.Id);
        await Db.SaveChangesAsync();
        Cache.Set(CacheKeys.ShiftAuthorization(user.Id), new[] { team.Id });

        var result = await _service.LeaveTeamAsync(team.Id, user.Id);

        result.Should().BeTrue();
        Cache.TryGetValue(CacheKeys.ShiftAuthorization(user.Id), out _).Should().BeFalse();
    }

    [HumansFact]
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        alphaDefinition.Assignments.Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = alphaDefinition.Id,
            TeamMemberId = alphaTeamMember.Id,
            TeamMember = alphaTeamMember,
            SlotIndex = 0,
            AssignedAt = Clock.GetCurrentInstant(),
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };

        await Db.TeamRoleDefinitions.AddRangeAsync(alphaDefinition, betaDefinition);
        await Db.SaveChangesAsync();

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

    [HumansFact]
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        definition.Assignments.Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = definition.Id,
            TeamMemberId = assignedTeamMember.Id,
            TeamMember = assignedTeamMember,
            SlotIndex = 1,
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        });

        Db.TeamRoleDefinitions.Add(definition);
        await Db.SaveChangesAsync();

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

    [HumansFact]
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

        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 500);

        var summary = result.Teams.Should().ContainSingle(t => t.Name == "Dept A").Subject;
        summary.PendingShiftSignupCount.Should().Be(2);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_ReturnsZeroPendingShifts_WhenNoActiveEvent()
    {
        var team = SeedTeam("Dept B");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id);

        var inactiveEvent = SeedEventSettings("Past Event", isActive: false);
        var rota = SeedRota(team.Id, inactiveEvent.Id, "Shifts");
        var shift = SeedShift(rota.Id);
        SeedShiftSignup(shift.Id, user.Id, SignupStatus.Pending);

        await Db.SaveChangesAsync();

        var result = await _service.GetAdminTeamListAsync(1, 500);

        var summary = result.Teams.Should().ContainSingle(t => t.Name == "Dept B").Subject;
        summary.PendingShiftSignupCount.Should().Be(0);
    }

    // ==========================================================================
    // DeleteTeamAsync — #494: revoke Google access when a team is soft-deleted
    // ==========================================================================

    [HumansFact]
    public async Task DeleteTeamAsync_ClosesActiveMemberships()
    {
        // Arrange
        var team = SeedTeam("Doomed Team");
        var alice = SeedUser(displayName: "Alice");
        var bob = SeedUser(displayName: "Bob");
        SeedTeamMember(team.Id, alice.Id);
        SeedTeamMember(team.Id, bob.Id);
        // Previously-left member should NOT be touched.
        var ghost = SeedUser(displayName: "Ghost");
        SeedTeamMember(team.Id, ghost.Id, leftAt: Clock.GetCurrentInstant() - Duration.FromDays(30));
        await Db.SaveChangesAsync();

        // Act
        await _service.DeleteTeamAsync(team.Id);

        // Service uses its own DbContext; detach trackers so assertions re-read
        // from the store rather than returning stale tracked entities.
        Db.ChangeTracker.Clear();

        // Assert — team soft-deleted
        var reloaded = await Db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == team.Id);
        reloaded!.IsActive.Should().BeFalse();

        // All previously-active memberships now have LeftAt set.
        var aliceMember = await Db.TeamMembers.AsNoTracking()
            .FirstAsync(tm => tm.TeamId == team.Id && tm.UserId == alice.Id);
        var bobMember = await Db.TeamMembers.AsNoTracking()
            .FirstAsync(tm => tm.TeamId == team.Id && tm.UserId == bob.Id);
        aliceMember.LeftAt.Should().NotBeNull();
        bobMember.LeftAt.Should().NotBeNull();
        aliceMember.LeftAt.Should().Be(Clock.GetCurrentInstant());
        bobMember.LeftAt.Should().Be(Clock.GetCurrentInstant());

        // Previously-left membership is unchanged (still has its original LeftAt).
        var ghostMember = await Db.TeamMembers.AsNoTracking()
            .FirstAsync(tm => tm.TeamId == team.Id && tm.UserId == ghost.Id);
        ghostMember.LeftAt.Should().Be(Clock.GetCurrentInstant() - Duration.FromDays(30));

        // DeactivateResourcesForTeamAsync is intentionally NOT called from DeleteTeamAsync:
        // flipping GoogleResource.IsActive here would make the next reconciliation tick
        // skip the resources and leave stale Google access in place. Deactivation happens
        // in the sync service after access has been revoked.
        await _teamResourceService.DidNotReceive().DeactivateResourcesForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<GoogleResourceType?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteTeamAsync_NoActiveMembers_StillSoftDeletes()
    {
        var team = SeedTeam("Empty Team");
        await Db.SaveChangesAsync();

        await _service.DeleteTeamAsync(team.Id);

        Db.ChangeTracker.Clear();

        var reloaded = await Db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == team.Id);
        reloaded!.IsActive.Should().BeFalse();

        await _teamResourceService.DidNotReceive().DeactivateResourcesForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<GoogleResourceType?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetExpectedAsync_DuplicateGoogleGroupEmail_SkipsClaim()
    {
        var alice = SeedUser(displayName: "Alice");
        var bob = SeedUser(displayName: "Bob");
        var first = SeedTeam("First");
        var second = SeedTeam("Second");
        first.GoogleGroupPrefix = "shared";
        second.GoogleGroupPrefix = "shared";
        SeedTeamMember(first.Id, alice.Id);
        SeedTeamMember(second.Id, bob.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetExpectedAsync();

        result.Should().NotContainKey($"shared@{DomainConstants.GoogleGroupDomain}");
    }

    // --- Helpers ---

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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.TeamRoleDefinitions.Add(definition);
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
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        };
        Db.TeamRoleAssignments.Add(assignment);
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.EventSettings.Add(es);
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.Rotas.Add(rota);
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.Shifts.Add(shift);
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.ShiftSignups.Add(signup);
        return signup;
    }
}


