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
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
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

    public TeamServiceTests()
    {
        _roleAssignmentService = new RoleAssignmentService(
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
        _teamResourceService = Substitute.For<ITeamResourceService>();
        _teamResourceService
            .GetTeamResourceSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamResourceSummary>());

        // Capture the user service to a local before threading it into the locator
        // builder — NSubstitute can't attach an outer .Returns() to a factory that
        // itself configures substitute calls.
        var userService = NewDbBackedUserService();
        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamService>()
            .With<IRoleAssignmentService>(_roleAssignmentService)
            .With<IEmailService>()
            .With<ISystemTeamSync>()
            .With(_teamResourceService)
            .With(userService)
            .Build();
        var shiftManagementService = new ShiftManagementService(
            new ShiftRepository(DbFactory, Db, Clock),
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        // Shift-auth invalidation: production path uses IShiftAuthorizationInvalidator
        // (backed by ShiftManagementService in DI). In tests we redirect it to the
        // same IMemoryCache entry so legacy assertions on the ShiftAuthorization
        // cache key keep working.
        ShiftAuthInvalidator
            .When(s => s.Invalidate(Arg.Any<Guid>()))
            .Do(ci => Cache.Remove(CacheKeys.ShiftAuthorization(ci.Arg<Guid>())));

        _service = new TeamService(
            new TeamRepository(DbFactory),
            AuditLog,
            Notifier,
            shiftManagementService,
            Substitute.For<INotificationMeterCacheInvalidator>(),
            ShiftAuthInvalidator,
            AdminAuthorization,
            serviceProvider,
            Clock,
            NullLogger<TeamService>.Instance);
    }

    // IsUserAdminAsync / IsUserBoardMemberAsync coverage lives in RoleAssignmentServiceTests.

    // ==========================================================================
    // CreateTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateTeamAsync_ReservedSlug_Throws()
    {
        var act = () => _service.CreateTeamAsync("Roster", null, requiresApproval: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reserved route*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_ParentNotFound_Throws()
    {
        var act = () => _service.CreateTeamAsync(
            "Child", null, requiresApproval: false, parentTeamId: Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_ParentIsSystemTeam_Throws()
    {
        var parent = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await Db.SaveChangesAsync();

        var act = () => _service.CreateTeamAsync(
            "Child", null, requiresApproval: false, parentTeamId: parent.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*System teams cannot be parents*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_ParentAlreadyHasParent_Throws()
    {
        var grandparent = SeedTeam("Department");
        var parent = SeedTeam("SubTeam");
        parent.ParentTeamId = grandparent.Id;
        await Db.SaveChangesAsync();

        var act = () => _service.CreateTeamAsync(
            "GrandChild", null, requiresApproval: false, parentTeamId: parent.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has a parent*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_PersistsAllFields()
    {
        var result = await _service.CreateTeamAsync(
            name: "Engineering",
            description: "Builds things",
            requiresApproval: true,
            parentTeamId: null,
            googleGroupPrefix: "eng",
            isHidden: true);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == result.Id);
        stored.Name.Should().Be("Engineering");
        stored.Description.Should().Be("Builds things");
        stored.Slug.Should().Be("engineering");
        stored.IsActive.Should().BeTrue();
        stored.RequiresApproval.Should().BeTrue();
        stored.IsHidden.Should().BeTrue();
        stored.GoogleGroupPrefix.Should().Be("eng");
        stored.ParentTeamId.Should().BeNull();
        stored.SystemTeamType.Should().Be(SystemTeamType.None);
        stored.CreatedAt.Should().Be(Clock.GetCurrentInstant());
        stored.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task CreateTeamAsync_ValidParent_PersistsParentId()
    {
        var parent = SeedTeam("Department");
        await Db.SaveChangesAsync();

        var result = await _service.CreateTeamAsync(
            "SubTeam", null, requiresApproval: false, parentTeamId: parent.Id);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == result.Id);
        stored.ParentTeamId.Should().Be(parent.Id);
    }

    [HumansFact]
    public async Task CreateTeamAsync_SlugCollision_AppendsSuffix()
    {
        var first = await _service.CreateTeamAsync("Alpha", null, requiresApproval: false);
        first.Slug.Should().Be("alpha");

        var second = await _service.CreateTeamAsync("Alpha", null, requiresApproval: false);

        second.Slug.Should().Be("alpha-2");
    }

    // ==========================================================================
    // AddSeededMemberAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddSeededMemberAsync_TeamNotFound_Throws()
    {
        var act = () => _service.AddSeededMemberAsync(
            Guid.NewGuid(), Guid.NewGuid(), TeamMemberRole.Member, Clock.GetCurrentInstant());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task AddSeededMemberAsync_SystemTeam_Throws()
    {
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        var user = SeedUser();
        await Db.SaveChangesAsync();

        var act = () => _service.AddSeededMemberAsync(
            team.Id, user.Id, TeamMemberRole.Member, Clock.GetCurrentInstant());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot target system teams*");
    }

    [HumansFact]
    public async Task AddSeededMemberAsync_AlreadyMember_Throws()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var act = () => _service.AddSeededMemberAsync(
            team.Id, user.Id, TeamMemberRole.Member, Clock.GetCurrentInstant());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already a member*");
    }

    [HumansFact]
    public async Task AddSeededMemberAsync_PersistsMemberWithRoleAndJoinedAt()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        await Db.SaveChangesAsync();
        var joinedAt = Instant.FromUtc(2025, 1, 1, 0, 0);

        var result = await _service.AddSeededMemberAsync(
            team.Id, user.Id, TeamMemberRole.Coordinator, joinedAt);

        Db.ChangeTracker.Clear();
        var stored = await Db.TeamMembers.AsNoTracking()
            .SingleAsync(m => m.TeamId == team.Id && m.UserId == user.Id);
        stored.Role.Should().Be(TeamMemberRole.Coordinator);
        stored.JoinedAt.Should().Be(joinedAt);
        stored.LeftAt.Should().BeNull();
        result.Id.Should().Be(stored.Id);
    }

    // ==========================================================================
    // GetActiveTeamMembershipsForUserAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetActiveTeamMembershipsForUserAsync_ReturnsActiveNonSystemMembershipWithHiddenFlag()
    {
        var user = SeedUser();
        var team = SeedTeam("Build");
        team.IsHidden = true;
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var result = await _service.GetActiveTeamMembershipsForUserAsync(user.Id);

        result.Should().ContainSingle();
        result[0].TeamName.Should().Be("Build");
        result[0].Role.Should().Be(TeamMemberRole.Coordinator);
        result[0].IsHidden.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetActiveTeamMembershipsForUserAsync_SkipsVolunteersSystemTeam()
    {
        var user = SeedUser();
        var vols = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        SeedTeamMember(vols.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.GetActiveTeamMembershipsForUserAsync(user.Id);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveTeamMembershipsForUserAsync_SkipsInactiveTeams()
    {
        var user = SeedUser();
        var team = SeedTeam("Old", isActive: false);
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.GetActiveTeamMembershipsForUserAsync(user.Id);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveTeamMembershipsForUserAsync_SkipsTeamsWhereUserIsNotMember()
    {
        var user = SeedUser();
        var other = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, other.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var result = await _service.GetActiveTeamMembershipsForUserAsync(user.Id);

        result.Should().BeEmpty();
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
    // UpdateTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateTeamAsync_TeamNotFound_Throws()
    {
        var act = () => _service.UpdateTeamAsync(
            Guid.NewGuid(), "name", null, requiresApproval: false, isActive: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_SystemTeam_UpdatesOnlyDescriptionAndPrefixAndReturnsEarly()
    {
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        team.Description = "old";
        team.RequiresApproval = false;
        await Db.SaveChangesAsync();
        var originalName = team.Name;
        var originalSlug = team.Slug;

        var result = await _service.UpdateTeamAsync(
            team.Id,
            name: "Renamed",
            description: "new",
            requiresApproval: true,
            isActive: false,
            googleGroupPrefix: "vol-prefix");

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.Description.Should().Be("new");
        stored.GoogleGroupPrefix.Should().Be("vol-prefix");
        // System teams ignore name/requiresApproval/isActive on update
        stored.Name.Should().Be(originalName);
        stored.Slug.Should().Be(originalSlug);
        stored.RequiresApproval.Should().BeFalse();
        stored.IsActive.Should().BeTrue();
        result.Description.Should().Be("new");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentIsSelf_Throws()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: team.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*own parent*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_TeamHasChildren_Throws()
    {
        var parent = SeedTeam("Parent");
        var child = SeedTeam("Child");
        child.ParentTeamId = parent.Id;
        var newParent = SeedTeam("NewParent");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            parent.Id, "Parent", null, false, true, parentTeamId: newParent.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*sub-teams*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentNotFound_Throws()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Parent team*not found*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentIsSystemTeam_Throws()
    {
        var team = SeedTeam("Alpha");
        var systemParent = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: systemParent.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*System teams cannot be parents*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentAlreadyHasParent_Throws()
    {
        var grandparent = SeedTeam("Grand");
        var parent = SeedTeam("Parent");
        parent.ParentTeamId = grandparent.Id;
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: parent.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*nest more than one level*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_InvalidCustomSlug_Throws()
    {
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, customSlug: "!@#$");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Custom slug is not valid*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_CustomSlugTakenByAnotherTeam_Throws()
    {
        SeedTeam("Other Team");
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, customSlug: "other-team");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in use*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_BlankCustomSlug_NormalizesToNull()
    {
        var team = SeedTeam("Alpha");
        team.CustomSlug = "previous-custom";
        await Db.SaveChangesAsync();

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, customSlug: "");

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.CustomSlug.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_RenamesTeam_AndRegeneratesSlug()
    {
        var team = SeedTeam("Old Name");
        await Db.SaveChangesAsync();

        await _service.UpdateTeamAsync(
            team.Id, "Brand New Name", null, false, true);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.Name.Should().Be("Brand New Name");
        stored.Slug.Should().Be("brand-new-name");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_RenameWithSlugTaken_KeepsOldSlug()
    {
        SeedTeam("Conflict");
        var team = SeedTeam("Old Name");
        await Db.SaveChangesAsync();
        var originalSlug = team.Slug;

        await _service.UpdateTeamAsync(
            team.Id, "Conflict", null, false, true);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.Name.Should().Be("Conflict");
        stored.Slug.Should().Be(originalSlug);
    }

    [HumansFact]
    public async Task UpdateTeamAsync_PersistsAllCoreFields()
    {
        var team = SeedTeam("Alpha");
        team.Description = "old";
        team.RequiresApproval = false;
        team.IsActive = true;
        team.IsPublicPage = false;
        await Db.SaveChangesAsync();

        await _service.UpdateTeamAsync(
            team.Id,
            "Alpha Renamed",
            "new description",
            requiresApproval: true,
            isActive: false,
            googleGroupPrefix: "alpha-prefix",
            hasBudget: true,
            isHidden: true,
            isSensitive: true,
            isPromotedToDirectory: true);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.Description.Should().Be("new description");
        stored.RequiresApproval.Should().BeTrue();
        stored.IsActive.Should().BeFalse();
        stored.GoogleGroupPrefix.Should().Be("alpha-prefix");
        stored.HasBudget.Should().BeTrue();
        stored.IsHidden.Should().BeTrue();
        stored.IsSensitive.Should().BeTrue();
        stored.IsPromotedToDirectory.Should().BeTrue();
        stored.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task UpdateTeamAsync_OptionalFlagsNull_LeavesExistingValues()
    {
        var team = SeedTeam("Alpha");
        team.HasBudget = true;
        team.IsHidden = true;
        team.IsSensitive = true;
        team.IsPromotedToDirectory = true;
        await Db.SaveChangesAsync();

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true,
            hasBudget: null, isHidden: null, isSensitive: null, isPromotedToDirectory: null);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.HasBudget.Should().BeTrue();
        stored.IsHidden.Should().BeTrue();
        stored.IsSensitive.Should().BeTrue();
        stored.IsPromotedToDirectory.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_BecomingChild_ForcesPublicPageFalse()
    {
        var parent = SeedTeam("Parent");
        var team = SeedTeam("Alpha");
        team.IsPublicPage = true;
        team.ShowCoordinatorsOnPublicPage = true;
        await Db.SaveChangesAsync();

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: parent.Id);

        Db.ChangeTracker.Clear();
        var stored = await Db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id);
        stored.IsPublicPage.Should().BeFalse();
        stored.ShowCoordinatorsOnPublicPage.Should().BeFalse();
        stored.ParentTeamId.Should().Be(parent.Id);
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentChange_InvalidatesShiftAuthForManagementUsers()
    {
        var parentOld = SeedTeam("Old Parent");
        var parentNew = SeedTeam("New Parent");
        var team = SeedTeam("Alpha");
        team.ParentTeamId = parentOld.Id;
        var manager = SeedUser(displayName: "Manager");
        var member = SeedTeamMember(team.Id, manager.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(team.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await Db.SaveChangesAsync();
        Cache.Set(CacheKeys.ShiftAuthorization(manager.Id), new[] { team.Id });

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: parentNew.Id);

        Cache.TryGetValue(CacheKeys.ShiftAuthorization(manager.Id), out _).Should().BeFalse();
    }

    // ==========================================================================
    // RequestToJoinTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task RequestToJoinTeamAsync_TeamNotFound_Throws()
    {
        var user = SeedUser();
        await Db.SaveChangesAsync();

        var act = () => _service.RequestToJoinTeamAsync(Guid.NewGuid(), user.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_SystemTeam_Throws()
    {
        var user = SeedUser();
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers, requiresApproval: true);
        await Db.SaveChangesAsync();

        var act = () => _service.RequestToJoinTeamAsync(team.Id, user.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_HiddenTeam_Throws()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        team.IsHidden = true;
        await Db.SaveChangesAsync();

        var act = () => _service.RequestToJoinTeamAsync(team.Id, user.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*hidden*");
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_TeamWithoutApproval_Throws()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.RequestToJoinTeamAsync(team.Id, user.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not require approval*");
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_AlreadyHasPendingRequest_Throws()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedJoinRequest(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.RequestToJoinTeamAsync(team.Id, user.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has a pending request*");
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_AlreadyMember_Throws()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.RequestToJoinTeamAsync(team.Id, user.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already a member*");
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_HappyPath_PersistsRequestWithMessage()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        await Db.SaveChangesAsync();

        var result = await _service.RequestToJoinTeamAsync(team.Id, user.Id, "Pick me");

        result.TeamId.Should().Be(team.Id);
        result.UserId.Should().Be(user.Id);
        result.Message.Should().Be("Pick me");
        result.Status.Should().Be(TeamJoinRequestStatus.Pending);
        result.RequestedAt.Should().Be(Clock.GetCurrentInstant());

        Db.ChangeTracker.Clear();
        var stored = await Db.TeamJoinRequests.AsNoTracking().SingleAsync();
        stored.Id.Should().Be(result.Id);
        stored.Message.Should().Be("Pick me");
    }

    // ==========================================================================
    // RemoveMemberAsync
    // ==========================================================================

    [HumansFact]
    public async Task RemoveMemberAsync_TeamNotFound_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        await Db.SaveChangesAsync();

        var act = () => _service.RemoveMemberAsync(Guid.NewGuid(), Guid.NewGuid(), actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task RemoveMemberAsync_SystemTeam_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        SeedTeamMember(team.Id, target.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.RemoveMemberAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [HumansFact]
    public async Task RemoveMemberAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, target.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.RemoveMemberAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task RemoveMemberAsync_TargetNotAMember_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, actor.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var act = () => _service.RemoveMemberAsync(team.Id, target.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a member*");
    }

    [HumansFact]
    public async Task RemoveMemberAsync_RegularMember_SetsLeftAtAndReturnsFalse()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, actor.Id, TeamMemberRole.Coordinator);
        var member = SeedTeamMember(team.Id, target.Id, TeamMemberRole.Member);
        await Db.SaveChangesAsync();

        var wasCoordinator = await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id);

        wasCoordinator.Should().BeFalse();
        Db.ChangeTracker.Clear();
        var reloaded = await Db.TeamMembers.AsNoTracking().SingleAsync(tm => tm.Id == member.Id);
        reloaded.LeftAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task RemoveMemberAsync_Coordinator_ReturnsTrue()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, target.Id, TeamMemberRole.Coordinator);
        await Db.SaveChangesAsync();

        var wasCoordinator = await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id);

        wasCoordinator.Should().BeTrue();
    }

    // ==========================================================================
    // RejectJoinRequestAsync
    // ==========================================================================

    [HumansFact]
    public async Task RejectJoinRequestAsync_RequestNotFound_Throws()
    {
        var approver = SeedUser(displayName: "Approver");
        await Db.SaveChangesAsync();

        var act = () => _service.RejectJoinRequestAsync(Guid.NewGuid(), approver.Id, "reason");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_ApproverLacksPermission_Throws()
    {
        var stranger = SeedUser(displayName: "Stranger");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.RejectJoinRequestAsync(request.Id, stranger.Id, "no thanks");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_RequestAlreadyApproved_Throws()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id, TeamJoinRequestStatus.Approved);
        await Db.SaveChangesAsync();

        var act = () => _service.RejectJoinRequestAsync(request.Id, coordinator.Id, "late");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pending*");
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_HappyPath_RecordsRejectionWithReason()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await Db.SaveChangesAsync();

        await _service.RejectJoinRequestAsync(request.Id, coordinator.Id, "out of capacity");

        Db.ChangeTracker.Clear();
        var stored = await Db.TeamJoinRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        stored.Status.Should().Be(TeamJoinRequestStatus.Rejected);
        stored.ReviewNotes.Should().Be("out of capacity");
        stored.ReviewedByUserId.Should().Be(coordinator.Id);
        stored.ResolvedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // ==========================================================================
    // CreateRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_TeamNotFound_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        await Db.SaveChangesAsync();

        var act = () => _service.CreateRoleDefinitionAsync(
            Guid.NewGuid(), "Lead", null, 1, [SlotPriority.None], 0, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Team*not found*");
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_DuplicateName_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        SeedTeamRoleDefinition(team.Id, isManagement: false);
        var existingName = "Member Role";
        await Db.SaveChangesAsync();

        var act = () => _service.CreateRoleDefinitionAsync(
            team.Id, existingName, null, 1, [SlotPriority.None], 0, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var act = () => _service.CreateRoleDefinitionAsync(
            team.Id, "Lead", null, 1, [SlotPriority.None], 0, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_HappyPath_PersistsDefinition()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        await Db.SaveChangesAsync();

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Lead", "Lead description", 2,
            [SlotPriority.Critical, SlotPriority.Important], 5, RolePeriod.Event, actor.Id,
            isPublic: false);

        result.TeamId.Should().Be(team.Id);
        result.Name.Should().Be("Lead");
        result.SlotCount.Should().Be(2);
        result.SortOrder.Should().Be(5);
        result.IsPublic.Should().BeFalse();
        result.Period.Should().Be(RolePeriod.Event);
        result.CreatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // ==========================================================================
    // UpdateRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_DefinitionNotFound_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            Guid.NewGuid(), "Lead", null, 1, [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role definition*not found*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            def.Id, "Lead", null, 1, [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_ReducingSlotCountBelowAssignmentsCount_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        def.SlotCount = 3;
        var user = SeedUser(displayName: "Holder");
        var member = SeedTeamMember(team.Id, user.Id);
        SeedTeamRoleAssignment(def.Id, member.Id);
        SeedTeamRoleAssignment(def.Id, member.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            def.Id, def.Name, null, slotCount: 1,
            [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot reduce slot count*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_RenamingToExistingName_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var first = SeedTeamRoleDefinition(team.Id, isManagement: false);
        first.Name = "Original";
        var second = SeedTeamRoleDefinition(team.Id, isManagement: false);
        second.Name = "Other";
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            second.Id, "Original", null, 1, [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_CanToggleManagementFalse_PreservesIsManagement()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: true);
        await Db.SaveChangesAsync();

        await _service.UpdateRoleDefinitionAsync(
            def.Id, def.Name, null, 1, [SlotPriority.None], 0,
            isManagement: false, RolePeriod.YearRound, actor.Id,
            canToggleManagement: false);

        Db.ChangeTracker.Clear();
        var stored = await Db.TeamRoleDefinitions.AsNoTracking().SingleAsync(d => d.Id == def.Id);
        stored.IsManagement.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_PromotingToManagementWhenAnotherExists_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var existingManagement = SeedTeamRoleDefinition(team.Id, isManagement: true);
        existingManagement.Name = "Existing Mgmt";
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        def.Name = "To Promote";
        await Db.SaveChangesAsync();

        var act = () => _service.UpdateRoleDefinitionAsync(
            def.Id, def.Name, null, 1, [SlotPriority.None], 0,
            isManagement: true, RolePeriod.YearRound, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already marked as the management role*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_HappyPath_UpdatesAllEditableFields()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        def.Name = "Original";
        def.SortOrder = 0;
        await Db.SaveChangesAsync();

        await _service.UpdateRoleDefinitionAsync(
            def.Id, "Renamed", "new desc", slotCount: 2,
            [SlotPriority.Critical, SlotPriority.None], sortOrder: 7,
            isManagement: false, RolePeriod.Event, actor.Id, isPublic: false);

        Db.ChangeTracker.Clear();
        var stored = await Db.TeamRoleDefinitions.AsNoTracking().SingleAsync(d => d.Id == def.Id);
        stored.Name.Should().Be("Renamed");
        stored.Description.Should().Be("new desc");
        stored.SlotCount.Should().Be(2);
        stored.SortOrder.Should().Be(7);
        stored.IsPublic.Should().BeFalse();
        stored.Period.Should().Be(RolePeriod.Event);
        stored.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // ==========================================================================
    // DeleteRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_NotFound_Throws()
    {
        var actor = SeedUser();
        await Db.SaveChangesAsync();

        var act = () => _service.DeleteRoleDefinitionAsync(Guid.NewGuid(), actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role definition*not found*");
    }

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_ManagementWithAssignedMembers_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: true);
        var holder = SeedUser(displayName: "Holder");
        var member = SeedTeamMember(team.Id, holder.Id);
        SeedTeamRoleAssignment(def.Id, member.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.DeleteRoleDefinitionAsync(def.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*management role while members are assigned*");
    }

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await Db.SaveChangesAsync();

        var act = () => _service.DeleteRoleDefinitionAsync(def.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_HappyPath_RemovesDefinition()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await Db.SaveChangesAsync();

        await _service.DeleteRoleDefinitionAsync(def.Id, actor.Id);

        Db.ChangeTracker.Clear();
        var stored = await Db.TeamRoleDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == def.Id);
        stored.Should().BeNull();
    }

    // ==========================================================================
    // ToggleRoleIsManagementAsync
    // ==========================================================================

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_DefinitionNotFound_Throws()
    {
        var actor = SeedUser();
        await Db.SaveChangesAsync();

        var act = () => _service.ToggleRoleIsManagementAsync(Guid.NewGuid(), actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role definition*not found*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await Db.SaveChangesAsync();

        var act = () => _service.ToggleRoleIsManagementAsync(def.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_PromotingWithAssignments_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        var holder = SeedUser(displayName: "Holder");
        var member = SeedTeamMember(team.Id, holder.Id);
        SeedTeamRoleAssignment(def.Id, member.Id);
        await Db.SaveChangesAsync();

        var act = () => _service.ToggleRoleIsManagementAsync(def.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*members are assigned*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_PromotingWhenAnotherManagementExists_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        SeedTeamRoleDefinition(team.Id, isManagement: true);
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await Db.SaveChangesAsync();

        var act = () => _service.ToggleRoleIsManagementAsync(def.Id, actor.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already marked as the management role*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_HappyPathPromote_FlipsIsManagement()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await Db.SaveChangesAsync();

        var result = await _service.ToggleRoleIsManagementAsync(def.Id, actor.Id);

        result.IsManagement.Should().BeTrue();
        Db.ChangeTracker.Clear();
        var stored = await Db.TeamRoleDefinitions.AsNoTracking().SingleAsync(d => d.Id == def.Id);
        stored.IsManagement.Should().BeTrue();
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_HappyPathDemote_FlipsIsManagement()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: true);
        await Db.SaveChangesAsync();

        var result = await _service.ToggleRoleIsManagementAsync(def.Id, actor.Id);

        result.IsManagement.Should().BeFalse();
    }

    // ==========================================================================
    // PermanentlyDeleteTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task PermanentlyDeleteTeamAsync_TeamNotFound_Throws()
    {
        var act = () => _service.PermanentlyDeleteTeamAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task PermanentlyDeleteTeamAsync_SystemTeam_Throws()
    {
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await Db.SaveChangesAsync();

        var act = () => _service.PermanentlyDeleteTeamAsync(team.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [HumansFact]
    public async Task PermanentlyDeleteTeamAsync_HasActiveChildren_Throws()
    {
        var parent = SeedTeam("Parent");
        var child = SeedTeam("Child");
        child.ParentTeamId = parent.Id;
        await Db.SaveChangesAsync();

        var act = () => _service.PermanentlyDeleteTeamAsync(parent.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*sub-teams*");
    }

    // Happy-path PermanentlyDeleteTeamAsync needs a real transactional store; the in-memory
    // EF provider used in this harness does not support transactions. Cover it in Integration tests.

    // ==========================================================================
    // GetTeamEntityBySlugAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamEntityBySlugAsync_ExistingSlug_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamEntityBySlugAsync("alpha");

        result.Should().NotBeNull();
        result.Name.Should().Be("Alpha");
        result.Members.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetTeamEntityBySlugAsync_NonExistentSlug_ReturnsNull()
    {
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamEntityBySlugAsync("non-existent");

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamEntityBySlugAsync_IncludesUserNavigation()
    {
        var user = SeedUser(displayName: "Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamEntityBySlugAsync("alpha");

        result!.Members.Single().User.Should().NotBeNull();
        result.Members.Single().User.DisplayName.Should().Be("Alice");
    }

    // ==========================================================================
    // GetTeamBySlugAsync (TeamInfo? — ITeamServiceRead surface)
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamBySlugAsync_ExistingSlug_ReturnsSameTeamAsEntityVersion()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await Db.SaveChangesAsync();

        var info = await _service.GetTeamBySlugAsync("alpha");
        var entity = await _service.GetTeamEntityBySlugAsync("alpha");

        info.Should().NotBeNull();
        entity.Should().NotBeNull();
        info!.Id.Should().Be(entity!.Id);
        info.Slug.Should().Be(entity.Slug);
        info.Name.Should().Be(entity.Name);
    }

    [HumansFact]
    public async Task GetTeamBySlugAsync_NonExistentSlug_ReturnsNull()
    {
        await Db.SaveChangesAsync();

        var result = await _service.GetTeamBySlugAsync("non-existent");

        result.Should().BeNull();
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


