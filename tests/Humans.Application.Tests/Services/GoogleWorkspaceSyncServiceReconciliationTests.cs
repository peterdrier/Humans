using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Tests.Fakes;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// End-to-end coverage for <see cref="GoogleWorkspaceSyncService.SyncResourcesByTypeAsync"/>
/// — the reconciliation path that PR #227 layered multiple fixes onto. See issue #508:
/// every scenario here guards against a specific bug that was found by review instead of by
/// a failing test.
/// </summary>
public sealed class GoogleWorkspaceSyncServiceReconciliationTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly HumansDbContext _dbContext;
    private readonly InMemoryDbContextFactory _dbContextFactory;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly StubTeamResourceService _teamResourceService;
    private readonly FakeSyncSettingsService _syncSettings;
    private readonly FakeGoogleGroupMembershipClient _groupClient = new();
    private readonly FakeGoogleDrivePermissionClient _driveClient = new();
    private readonly IServiceProvider _serviceProvider;

    public GoogleWorkspaceSyncServiceReconciliationTests()
    {
        _dbContext = BuildDbContext(_dbName);
        _dbContextFactory = new InMemoryDbContextFactory(_dbName);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 16, 12, 0));
        _auditLogService = Substitute.For<IAuditLogService>();
        _syncSettings = new FakeSyncSettingsService();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IAuditLogService)).Returns(_auditLogService);
        serviceProvider.GetService(typeof(ITeamService)).Returns(Substitute.For<ITeamService>());
        _teamResourceService = new StubTeamResourceService(
            _dbContext,
            Options.Create(new TeamResourceManagementSettings()),
            serviceProvider,
            Substitute.For<IRoleAssignmentService>(),
            _clock,
            NullLogger<StubTeamResourceService>.Instance);
        serviceProvider.GetService(typeof(ITeamResourceService)).Returns(_teamResourceService);
        _serviceProvider = serviceProvider;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    // -------------------------------------------------------------------------
    // Scenario 1: Soft-deleted team — revoke Drive + Group, deactivate per type
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_DriveFolderPass_RevokesPermissionsAndDeactivatesOnlyDriveRow()
    {
        // Arrange: one soft-deleted team with a Drive folder + Group, both
        // still IsActive = true. Both members have active Google access that
        // should be revoked on the first reconciliation tick.
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        var bob = SeedUser("bob@nobodies.team", "Bob");
        SeedLeftMember(team.Id, alice.Id);
        SeedLeftMember(team.Id, bob.Id);

        var driveFolder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "drive-soft-delete");
        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-soft-delete");
        _driveClient.SeedDirectPermission(driveFolder.GoogleId, alice.Email!);
        _driveClient.SeedDirectPermission(driveFolder.GoogleId, bob.Email!);
        _groupClient.SeedMembership(group.GoogleId, alice.Email!);
        _groupClient.SeedMembership(group.GoogleId, bob.Email!);

        await _dbContext.SaveChangesAsync();

        var service = BuildService();

        // Act: run only the DriveFolder pass.
        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        // Assert: diff has no error; Drive permissions are gone from the fake.
        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().BeNull();
        _driveClient.GetPermissions(driveFolder.GoogleId).Should().BeEmpty();
        _groupClient.GetMemberships(group.GoogleId).Should().HaveCount(2,
            "the Group pass has not run yet and Drive reconciliation must not touch Group state");

        // The DriveFolder row is deactivated; the Group row is NOT — this is the
        // per-type scoping that guards the Drive-then-Group ordering.
        var rows = await _dbContext.GoogleResources.AsNoTracking()
            .Where(r => r.TeamId == team.Id)
            .ToListAsync();
        rows.Single(r => r.ResourceType == GoogleResourceType.DriveFolder).IsActive.Should().BeFalse();
        rows.Single(r => r.ResourceType == GoogleResourceType.Group).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SoftDeletedTeam_GroupPass_RevokesMembershipsAndDeactivatesGroupRow()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-only");
        _groupClient.SeedMembership(group.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().BeNull();
        _groupClient.GetMemberships(group.GoogleId).Should().BeEmpty();

        var row = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.TeamId == team.Id);
        row.IsActive.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Scenario 2: Shared GoogleId Drive group — error defers ALL rows
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SharedGoogleIdDriveGroup_WhenListFails_NoTeamInGroupIsDeactivated()
    {
        // Arrange: two soft-deleted teams both linked to the same Drive folder.
        // The fake throws on ListPermissionsAsync for that GoogleId, so the
        // diff's ErrorMessage is non-null. Neither row may be deactivated —
        // otherwise the drive row for the "secondary" team is lost forever
        // because SyncDriveResourceGroupAsync only surfaces the primary ResourceId.
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var teamA = SeedTeam(softDeleted: true, slug: "team-a");
        var teamB = SeedTeam(softDeleted: true, slug: "team-b");
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(teamA.Id, alice.Id);
        SeedLeftMember(teamB.Id, alice.Id);

        const string sharedFolderId = "shared-folder-fail";
        SeedResource(teamA.Id, GoogleResourceType.DriveFolder, sharedFolderId);
        SeedResource(teamB.Id, GoogleResourceType.DriveFolder, sharedFolderId);
        _driveClient.FailFile(sharedFolderId);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        // Diff carries the error; both rows stay IsActive = true.
        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().NotBeNullOrEmpty();

        var rows = await _dbContext.GoogleResources.AsNoTracking()
            .Where(r => r.GoogleId == sharedFolderId)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.IsActive.Should().BeTrue(
            "a failed Drive group must leave every row in the group active so the next tick can retry"));
    }

    // -------------------------------------------------------------------------
    // Scenario 3: Partial error within one team defers deactivation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_WithOneErroredAndOneCleanDrive_DefersDeactivationForBoth()
    {
        // One soft-deleted team with TWO DriveFolder rows pointing at different
        // GoogleIds. The fake errors on folder-a but succeeds on folder-b.
        // DeactivateResourcesForTeamAsync(team, DriveFolder) is all-or-nothing
        // by design, so the clean row must NOT be swept inactive — otherwise a
        // follow-up call to DeactivateResourcesForTeamAsync would also flip the
        // errored row and its stale access would be lost forever.
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        const string erroredFolder = "folder-a-errors";
        const string cleanFolder = "folder-b-ok";
        SeedResource(team.Id, GoogleResourceType.DriveFolder, erroredFolder);
        SeedResource(team.Id, GoogleResourceType.DriveFolder, cleanFolder);
        _driveClient.SeedDirectPermission(cleanFolder, alice.Email!);
        _driveClient.FailFile(erroredFolder);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        result.Diffs.Should().HaveCount(2);
        // Clean folder had its permission revoked...
        _driveClient.GetPermissions(cleanFolder).Should().BeEmpty();
        // ...but neither row is deactivated because the team has a same-type errored row.
        var rows = await _dbContext.GoogleResources.AsNoTracking()
            .Where(r => r.TeamId == team.Id)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.IsActive.Should().BeTrue(
            "a partial error within one team+type defers the entire team from deactivation"));
    }

    // -------------------------------------------------------------------------
    // Scenario 4: DriveFolder pass must NOT deactivate Group row
    // -------------------------------------------------------------------------
    // Merged with scenario 1 — the first test already asserts that the Group row
    // stays IsActive = true after the DriveFolder pass.

    // -------------------------------------------------------------------------
    // Scenario 5: AddOnly mode — soft-deleted team
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_DriveInAddOnlyMode_DoesNotRevokeOrDeactivate()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddOnly);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-addonly");
        _driveClient.SeedDirectPermission(folder.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        result.Diffs.Should().ContainSingle()
            .Which.ErrorMessage.Should().BeNull();
        _driveClient.GetPermissions(folder.GoogleId).Should().ContainSingle(p => p.EmailAddress == alice.Email);
        var row = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == folder.Id);
        row.IsActive.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Scenario 6: None mode — soft-deleted team
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeletedTeam_DriveInNoneMode_DoesNotRevokeOrDeactivate()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.None);
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam(softDeleted: true);
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedLeftMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-none");
        _driveClient.SeedDirectPermission(folder.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        result.Diffs.Should().ContainSingle().Which.ErrorMessage.Should().BeNull();
        _driveClient.GetPermissions(folder.GoogleId).Should().ContainSingle(p => p.EmailAddress == alice.Email);
        var row = await _dbContext.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == folder.Id);
        row.IsActive.Should().BeTrue();
    }

    // =========================================================================
    // ADD PATH — missing members get provisioned
    // =========================================================================

    [Fact]
    public async Task MissingGroupMember_IsAddedOnExecute()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-add");
        // No membership seeded in the fake — Alice is Missing.

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        _groupClient.GetMemberships(group.GoogleId)
            .Should().ContainSingle(m => m.PreferredMemberKey!.Id == alice.Email);
    }

    [Fact]
    public async Task MissingDriveMember_IsAddedWithCorrectRole()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-add",
            driveLevel: DrivePermissionLevel.ContentManager);
        // No permission seeded — Alice is Missing.

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        var perms = _driveClient.GetPermissions(folder.GoogleId);
        perms.Should().ContainSingle(p =>
            p.EmailAddress == alice.Email && p.Role == "fileOrganizer");
    }

    // =========================================================================
    // NO-OP PATH — correct state, no side effects
    // =========================================================================

    [Fact]
    public async Task CorrectGroupMember_NoMutationsOccur()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-noop");
        _groupClient.SeedMembership(group.GoogleId, alice.Email!);

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        var diff = result.Diffs.Should().ContainSingle().Subject;
        diff.ErrorMessage.Should().BeNull();
        diff.Members.Should().ContainSingle(m => m.State == MemberSyncState.Correct);
        _groupClient.GetMemberships(group.GoogleId).Should().ContainSingle();
    }

    [Fact]
    public async Task CorrectDriveMember_NoMutationsOccur()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-noop");
        _driveClient.SeedDirectPermission(folder.GoogleId, alice.Email!, "writer");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        var diff = result.Diffs.Should().ContainSingle().Subject;
        diff.ErrorMessage.Should().BeNull();
        diff.Members.Should().ContainSingle(m => m.State == MemberSyncState.Correct);
        _driveClient.GetPermissions(folder.GoogleId).Should().ContainSingle();
    }

    // =========================================================================
    // REMOVE PATH — extra members on active team
    // =========================================================================

    [Fact]
    public async Task ExtraGroupMember_IsRemovedOnExecute()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-extra");
        _groupClient.SeedMembership(group.GoogleId, "stranger@example.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        _groupClient.GetMemberships(group.GoogleId).Should().BeEmpty();
    }

    [Fact]
    public async Task ExtraDirectDrivePermission_IsRemovedOnExecute()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-extra");
        _driveClient.SeedDirectPermission(folder.GoogleId, "stranger@example.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        _driveClient.GetPermissions(folder.GoogleId).Should().BeEmpty();
    }

    // =========================================================================
    // WRONG ROLE — member at wrong Drive permission level gets upgraded
    // =========================================================================

    [Fact]
    public async Task DriveMember_AtWrongRole_IsUpgraded()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-role",
            driveLevel: DrivePermissionLevel.ContentManager);
        // Alice is at "reader" (Viewer) but the resource expects ContentManager ("fileOrganizer").
        _driveClient.SeedDirectPermission(folder.GoogleId, alice.Email!, "reader");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        var diff = result.Diffs.Should().ContainSingle().Subject;
        diff.Members.Should().ContainSingle(m => m.State == MemberSyncState.WrongRole);

        // After execute, the fake should have a new permission at the correct level
        // (the old one stays — real Google API replaces; our fake just adds, which is fine
        // for verifying the upgrade was attempted with the correct role).
        _driveClient.GetPermissions(folder.GoogleId)
            .Should().Contain(p => p.Role == "fileOrganizer" && p.EmailAddress == alice.Email);
    }

    // =========================================================================
    // MIXED TRANSITIONS — correct + missing + extra in one tick
    // =========================================================================

    [Fact]
    public async Task GroupReconciliation_MixedState_AddsRemovesAndKeeps()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        var bob = SeedUser("bob@nobodies.team", "Bob");
        SeedActiveMember(team.Id, alice.Id);
        SeedActiveMember(team.Id, bob.Id);

        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-mixed");
        _groupClient.SeedMembership(group.GoogleId, alice.Email!);
        _groupClient.SeedMembership(group.GoogleId, "stranger@example.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        var diff = result.Diffs.Should().ContainSingle().Subject;
        diff.Members.Should().Contain(m => m.Email == alice.Email && m.State == MemberSyncState.Correct);
        diff.Members.Should().Contain(m => m.Email == bob.Email && m.State == MemberSyncState.Missing);
        diff.Members.Should().Contain(m => m.Email == "stranger@example.com" && m.State == MemberSyncState.Extra);

        var remaining = _groupClient.GetMemberships(group.GoogleId);
        remaining.Should().HaveCount(2);
        remaining.Select(m => m.PreferredMemberKey!.Id).Should().Contain(alice.Email)
            .And.Contain(bob.Email)
            .And.NotContain("stranger@example.com");
    }

    // =========================================================================
    // SHARED GoogleId UNION — Drive folder linked to two teams
    // =========================================================================

    [Fact]
    public async Task SharedGoogleIdDrive_UnionOfTeamMembersIsExpected()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var teamA = SeedTeam(slug: "alpha");
        var teamB = SeedTeam(slug: "bravo");
        var alice = SeedUser("alice@nobodies.team", "Alice");
        var bob = SeedUser("bob@nobodies.team", "Bob");
        SeedActiveMember(teamA.Id, alice.Id);
        SeedActiveMember(teamB.Id, bob.Id);

        const string sharedFolder = "shared-folder-union";
        SeedResource(teamA.Id, GoogleResourceType.DriveFolder, sharedFolder);
        SeedResource(teamB.Id, GoogleResourceType.DriveFolder, sharedFolder);
        // Neither has a permission yet — both Alice and Bob should be added.

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        var perms = _driveClient.GetPermissions(sharedFolder);
        perms.Select(p => p.EmailAddress).Should().Contain(alice.Email).And.Contain(bob.Email);
    }

    [Fact]
    public async Task SharedGoogleIdDrive_MaxPermissionLevelWins()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var teamA = SeedTeam(slug: "alpha-lv");
        var teamB = SeedTeam(slug: "bravo-lv");
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(teamA.Id, alice.Id);
        SeedActiveMember(teamB.Id, alice.Id);

        const string sharedFolder = "shared-folder-level";
        SeedResource(teamA.Id, GoogleResourceType.DriveFolder, sharedFolder,
            driveLevel: DrivePermissionLevel.Viewer);
        SeedResource(teamB.Id, GoogleResourceType.DriveFolder, sharedFolder,
            driveLevel: DrivePermissionLevel.ContentManager);
        // No existing permission — Alice should be added at max (ContentManager → fileOrganizer).

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        _driveClient.GetPermissions(sharedFolder)
            .Should().ContainSingle(p =>
                p.EmailAddress == alice.Email && p.Role == "fileOrganizer");
    }

    // =========================================================================
    // INHERITED PERMISSION — never flagged as Extra or removed
    // =========================================================================

    [Fact]
    public async Task InheritedDrivePermission_IsNotTreatedAsExtra()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var folder = SeedResource(team.Id, GoogleResourceType.DriveFolder, "folder-inherited");
        _driveClient.SeedInheritedPermission(folder.GoogleId, "shared-drive-user@example.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute);

        var diff = result.Diffs.Should().ContainSingle().Subject;
        diff.Members.Should().ContainSingle(m =>
            m.Email == "shared-drive-user@example.com" && m.State == MemberSyncState.Inherited);
        _driveClient.GetPermissions(folder.GoogleId).Should().ContainSingle(
            "inherited permissions must never be deleted by reconciliation");
    }

    // =========================================================================
    // AddOnly MODE — adds missing but does NOT remove extras
    // =========================================================================

    [Fact]
    public async Task AddOnlyMode_AddsMissingButKeepsExtras()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddOnly);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-addonly");
        _groupClient.SeedMembership(group.GoogleId, "stranger@example.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        var members = _groupClient.GetMemberships(group.GoogleId);
        members.Should().HaveCount(2);
        members.Select(m => m.PreferredMemberKey!.Id)
            .Should().Contain(alice.Email, "missing member should be added in AddOnly mode")
            .And.Contain("stranger@example.com", "extra member should NOT be removed in AddOnly mode");
    }

    // =========================================================================
    // SUB-TEAM ROLLUP — child team members expected in parent's resource
    // =========================================================================

    [Fact]
    public async Task SubTeamRollup_ChildMembersAreExpectedInParentGroup()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var department = SeedTeam(slug: "dept");
        var childTeam = SeedTeam(slug: "child");
        childTeam.ParentTeamId = department.Id;

        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(childTeam.Id, alice.Id);

        var group = SeedResource(department.Id, GoogleResourceType.Group, "group-dept");
        // Alice is not on the department team directly, but is on a child team.

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        _groupClient.GetMemberships(group.GoogleId)
            .Should().ContainSingle(m => m.PreferredMemberKey!.Id == alice.Email,
                "child team members must be rolled up into the parent's Google resource");
    }

    // =========================================================================
    // SERVICE ACCOUNT FILTER — SA email not flagged as Extra
    // =========================================================================

    [Fact]
    public async Task ServiceAccountEmail_IsNotFlaggedAsExtra()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var group = SeedResource(team.Id, GoogleResourceType.Group, "group-sa");
        // The SA email comes from GetServiceAccountEmailAsync which returns
        // "unknown@serviceaccount.iam.gserviceaccount.com" when no key is configured.
        _groupClient.SeedMembership(group.GoogleId, "unknown@serviceaccount.iam.gserviceaccount.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        var result = await service.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute);

        var diff = result.Diffs.Should().ContainSingle().Subject;
        diff.Members.Should().BeEmpty("the SA email should be silently excluded from the diff");
        _groupClient.GetMemberships(group.GoogleId).Should().ContainSingle(
            "SA membership must not be removed by reconciliation");
    }

    // =========================================================================
    // DriveFile PARITY — same code path as DriveFolder
    // =========================================================================

    [Fact]
    public async Task DriveFile_ReconcilesIdenticallyToDriveFolder()
    {
        _syncSettings.SetMode(SyncServiceType.GoogleDrive, SyncMode.AddAndRemove);

        var team = SeedTeam();
        var alice = SeedUser("alice@nobodies.team", "Alice");
        SeedActiveMember(team.Id, alice.Id);

        var file = SeedResource(team.Id, GoogleResourceType.DriveFile, "file-parity");
        _driveClient.SeedDirectPermission(file.GoogleId, "stranger@example.com");

        await _dbContext.SaveChangesAsync();
        var service = BuildService();

        await service.SyncResourcesByTypeAsync(GoogleResourceType.DriveFile, SyncAction.Execute);

        var perms = _driveClient.GetPermissions(file.GoogleId);
        perms.Should().ContainSingle(p => p.EmailAddress == alice.Email,
            "missing member should be added on DriveFile just like DriveFolder");
        perms.Should().NotContain(p => p.EmailAddress == "stranger@example.com",
            "extra permission should be removed on DriveFile just like DriveFolder");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private GoogleWorkspaceSyncService BuildService()
    {
        return new GoogleWorkspaceSyncService(
            _dbContext,
            _dbContextFactory,
            Options.Create(new GoogleWorkspaceSettings()),
            _clock,
            _auditLogService,
            _syncSettings,
            _serviceProvider,
            NullLogger<GoogleWorkspaceSyncService>.Instance,
            _groupClient,
            _driveClient);
    }

    private static HumansDbContext BuildDbContext(string name)
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new HumansDbContext(options);
    }

    private Team SeedTeam(bool softDeleted = false, string? slug = null)
    {
        var now = _clock.GetCurrentInstant();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = slug ?? $"Team-{Guid.NewGuid():N}"[..12],
            Slug = slug ?? Guid.NewGuid().ToString("N")[..12],
            IsActive = !softDeleted,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private User SeedUser(string email, string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            DisplayName = displayName,
            GoogleEmail = email,
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private void SeedActiveMember(Guid teamId, Guid userId)
    {
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            JoinedAt = _clock.GetCurrentInstant() - Duration.FromDays(30)
        });
    }

    private void SeedLeftMember(Guid teamId, Guid userId)
    {
        var now = _clock.GetCurrentInstant();
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            JoinedAt = now - Duration.FromDays(30),
            LeftAt = now - Duration.FromDays(1)
        });
    }

    private GoogleResource SeedResource(Guid teamId, GoogleResourceType type, string googleId,
        DrivePermissionLevel driveLevel = DrivePermissionLevel.Contributor)
    {
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = $"{type}-{googleId}",
            GoogleId = googleId,
            Url = $"https://example.com/{googleId}",
            ResourceType = type,
            IsActive = true,
            DrivePermissionLevel = type == GoogleResourceType.Group
                ? DrivePermissionLevel.None
                : driveLevel,
            ProvisionedAt = _clock.GetCurrentInstant() - Duration.FromDays(10)
        };
        _dbContext.GoogleResources.Add(resource);
        return resource;
    }

    // Mutable ISyncSettingsService impl so tests can flip modes without a mock setup call.
    private sealed class FakeSyncSettingsService : ISyncSettingsService
    {
        private readonly Dictionary<SyncServiceType, SyncMode> _modes = new();

        public void SetMode(SyncServiceType serviceType, SyncMode mode) => _modes[serviceType] = mode;

        public Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
            => Task.FromResult(_modes.TryGetValue(serviceType, out var mode) ? mode : SyncMode.None);

        public Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_modes
                .Select(kv => new SyncServiceSettings { ServiceType = kv.Key, SyncMode = kv.Value })
                .ToList());

        public Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default)
        {
            _modes[serviceType] = mode;
            return Task.CompletedTask;
        }
    }

    // The sync service's parallel reconciliation path pulls a fresh DbContext per task from
    // the factory. The InMemory provider keys databases by name, so handing out new instances
    // pointing at the same name keeps state coherent with the scoped _dbContext in the test.
    private sealed class InMemoryDbContextFactory : IDbContextFactory<HumansDbContext>
    {
        private readonly string _name;

        public InMemoryDbContextFactory(string name) => _name = name;

        public HumansDbContext CreateDbContext() => BuildDbContext(_name);
    }
}
