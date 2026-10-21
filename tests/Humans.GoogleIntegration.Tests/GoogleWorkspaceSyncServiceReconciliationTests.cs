using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// End-to-end regression coverage for the soft-deleted-team revocation path in
/// <see cref="GoogleWorkspaceSyncService.SyncResourcesByTypeAsync"/>, per
/// nobodies-collective/Humans#508. This file seeds a real (InMemory) <c>GoogleIntegrationDbContext</c>
/// through the real <see cref="GoogleResourceRepository"/> and a real
/// <see cref="TeamResourceService"/>, and drives the unified Drive engine against the
/// existing dev/test <see cref="StubGoogleDrivePermissionsClient"/> fake — no
/// new fake Google layer was needed; reuse-first applies to test
/// infrastructure too (memory/process/reuse-first-change-discipline.md).
///
/// <para>
/// Scope note: <see cref="GoogleResourceType.Group"/> is out of scope here —
/// <c>SyncResourcesByTypeAsync</c> throws for <c>Group</c> and delegates all
/// Group membership reconciliation to <c>IGoogleGroupSync</c> (see
/// <c>RequestGoogleGroupSyncAsync</c> / <c>ReconcileGroupResourceAsync</c> /
/// <c>EnsureTeamGroupAsync</c> — none of which run inside
/// <c>SyncResourcesByTypeAsync</c>). That path is already pinned by
/// <c>GoogleGroupSyncServiceTests</c>. Building an unused fake
/// CloudIdentityService for a code path this method never calls would be
/// speculative test infra the actual method can't exercise.
/// </para>
/// </summary>
public sealed class GoogleWorkspaceSyncServiceReconciliationTests : Infrastructure.GoogleIntegrationTestHarness
{
    private readonly StubGoogleDrivePermissionsClient _drivePermissions =
        new(NullLogger<StubGoogleDrivePermissionsClient>.Instance);

    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IWorkspaceIdentityManagement _identities;
    private readonly IGoogleDriveAccessSource _driveSource = Substitute.For<IGoogleDriveAccessSource>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IGoogleRemovalNotificationService _removalNotifications = Substitute.For<IGoogleRemovalNotificationService>();
    private readonly ITeamResourceGoogleClient _teamResourceClient = Substitute.For<ITeamResourceGoogleClient>();
    private readonly IGoogleSyncLogService _syncLog = Substitute.For<IGoogleSyncLogService>();

    private readonly GoogleWorkspaceSyncService _syncService;

    public GoogleWorkspaceSyncServiceReconciliationTests()
        : base(Instant.FromUtc(2026, 8, 2, 9, 0))
    {
        _identities = WorkspaceIdentityFakes.ResolvingThrough(_userService);
        _teamResourceClient.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>())
            .Returns("sa@nobodies.team");

        // No expected members and no non-member permissions in most scenarios,
        // so these two are only exercised by the "extra permission" cases —
        // safe defaults keep every scenario from needing to restub them.
        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _userEmailService.GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());

        // Default: full Drive reconciliation (add + remove) enabled.
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);

        IGoogleResourceRepository repository = new GoogleResourceRepository(GoogleIntegrationDbFactory);

        // Real TeamResourceService backed by the same in-memory DbContext
        // as the resource repository above — so
        // deactivation writes land in the same table the test asserts against.
        var teamResourceService = new TeamResourceService(
            repository,
            googleClient: Substitute.For<ITeamResourceGoogleClient>(),
            drivePermissions: _drivePermissions,
            teamService: Substitute.For<ITeamService>(),
            serviceProvider: new ServiceLocatorBuilder().Build(),
            auditLogService: AuditLog,
            resourceOptions: new TeamResourceManagementOptions(),
            clock: Clock,
            logger: NullLogger<TeamResourceService>.Instance);

        _driveSource.GetExpectedAccessAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(GoogleIntegrationDb.GoogleResources.Local
                .Where(r => r.IsActive && r.ResourceType != GoogleResourceType.Group
                    && (call.ArgAt<string?>(0) is null || string.Equals(r.GoogleId, call.ArgAt<string?>(0), StringComparison.Ordinal)))
                .Select(r => r.GoogleId).Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => new GoogleDriveAccessClaim([]), StringComparer.Ordinal)));
        var driveSync = new GoogleDriveAccessSyncService(
            [_driveSource], _drivePermissions, _userService, _identities, _userEmailService, _syncSettingsService,
            _syncLog, _removalNotifications, repository, _teamService, teamResourceService,
            Clock, NullLogger<GoogleDriveAccessSyncService>.Instance);

        var options = Options.Create(new GoogleWorkspaceOptions { Domain = "nobodies.team" });

        _syncService = new GoogleWorkspaceSyncService(
            Substitute.For<IGoogleGroupProvisioningClient>(),
            _drivePermissions,
            Substitute.For<IGoogleDirectoryClient>(),
            repository,
            Substitute.For<IGoogleSyncOutboxRepository>(),
            _teamService,
            _userService, _identities,
            Substitute.For<IGoogleGroupSync>(),
            driveSync,
            [_driveSource],
            AuditLog,
            _syncLog,
            _syncSettingsService,
            Substitute.For<IGoogleDriveAccessSyncScheduler>(),
            options,
            Clock,
            NullLogger<GoogleWorkspaceSyncService>.Instance);
    }

    // ==========================================================================
    // 1. Soft-deleted team revocation
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_SoftDeletedTeamWithExtraPermission_RevokesAccessAndDeactivatesResource()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        var googleId = CreateGoogleFolder("Doomed Folder");
        const string extraEmail = "ex-member@nobodies.team";

        // Simulate a permission that survived from when the team had members.
        await _drivePermissions.CreatePermissionAsync(googleId, extraEmail, "writer", ct);

        var resourceId = SeedDriveResource(teamId, googleId, "Doomed Folder");
        await SaveAllAsync(ct);

        // Team.IsActive = false + no active members (every TeamMember.LeftAt set).
        StubTeams((teamId, IsActive: false, Members: []));

        var result = await _syncService.SyncResourcesByTypeAsync(
            GoogleResourceType.DriveFolder,
            SyncAction.Execute,
            ct,
            GoogleSyncSource.ScheduledSync);

        result.Diffs.Should().ContainSingle(d => d.ErrorMessage == null);

        var permissions = await _drivePermissions.ListPermissionsAsync(googleId, ct);
        permissions.Permissions.Should().BeEmpty("the extra permission should have been revoked");

        var row = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
        row.IsActive.Should().BeFalse("the soft-deleted team's Drive resource should be deactivated for this type");

        await _removalNotifications.Received(1).NotifyRemovalAsync(
            extraEmail, GoogleResourceType.DriveFolder, "Doomed Folder", Arg.Any<string?>(),
            Arg.Any<SyncRemovalReason>(), Arg.Any<CancellationToken>());
        await _syncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked,
            resourceId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            extraEmail,
            Arg.Any<string>(),
            GoogleSyncSource.ScheduledSync,
            success: true,
            errorMessage: Arg.Any<string?>(),
            userId: Arg.Any<Guid?>(),
            ct: Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // 2. Shared-GoogleId Drive groups: whole-group error blocks every row
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_SharedGoogleIdGroupErrors_NoRowInGroupIsDeactivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId1 = Guid.NewGuid();
        var teamId2 = Guid.NewGuid();

        // Both teams' rows point at the same GoogleId, which is never
        // registered with the Drive stub — ListPermissionsAsync returns a
        // 404 GoogleClientError, exactly like a deleted/mistyped Drive item.
        const string sharedGoogleId = "unregistered-shared-drive-id";
        var resourceId1 = SeedDriveResource(teamId1, sharedGoogleId, "Shared Folder (Team 1)");
        var resourceId2 = SeedDriveResource(teamId2, sharedGoogleId, "Shared Folder (Team 2)");
        await SaveAllAsync(ct);

        StubTeams(
            (teamId1, IsActive: false, Members: []),
            (teamId2, IsActive: false, Members: []));

        var result = await _syncService.SyncResourcesByTypeAsync(
            GoogleResourceType.DriveFolder, SyncAction.Execute, ct);

        result.Diffs.Should().ContainSingle(d => d.ErrorMessage != null);

        var rows = await GoogleIntegrationDb.GoogleResources.AsNoTracking()
            .Where(r => r.Id == resourceId1 || r.Id == resourceId2)
            .ToListAsync(ct);
        rows.Should().OnlyContain(r => r.IsActive, "no row in an errored GoogleId group may be deactivated");
        rows.Should().OnlyContain(r => r.ErrorMessage != null);
    }

    // ==========================================================================
    // 3. Partial error within one team: neither row for that team deactivates
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_PartialErrorWithinTeam_DefersDeactivationForBothRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();

        var cleanGoogleId = CreateGoogleFolder("Clean Folder");
        const string erroredGoogleId = "unregistered-errored-folder-id";

        var cleanResourceId = SeedDriveResource(teamId, cleanGoogleId, "Clean Folder");
        var erroredResourceId = SeedDriveResource(teamId, erroredGoogleId, "Errored Folder");
        await SaveAllAsync(ct);

        StubTeams((teamId, IsActive: false, Members: []));

        await _syncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, ct);

        var rows = await GoogleIntegrationDb.GoogleResources.AsNoTracking()
            .Where(r => r.Id == cleanResourceId || r.Id == erroredResourceId)
            .ToListAsync(ct);
        rows.Should().OnlyContain(r => r.IsActive,
            "DeactivateResourcesForTeamAsync(team, type) is all-or-nothing; one errored resource must block the clean one too");
    }

    // ==========================================================================
    // 4. Type ordering: DriveFolder pass leaves Group/DriveFile rows alone
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_DriveFolderPass_DoesNotDeactivateGroupOrDriveFileRowsForSameTeam()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();

        var googleId = CreateGoogleFolder("Folder");
        var folderResourceId = SeedDriveResource(teamId, googleId, "Folder", GoogleResourceType.DriveFolder);
        var groupResourceId = SeedDriveResource(teamId, "group-id", "Group", GoogleResourceType.Group);
        var driveFileResourceId = SeedDriveResource(teamId, "file-id", "File", GoogleResourceType.DriveFile);
        await SaveAllAsync(ct);

        StubTeams((teamId, IsActive: false, Members: []));

        await _syncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, ct);

        var folderRow = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == folderResourceId, ct);
        folderRow.IsActive.Should().BeFalse("the DriveFolder row for the soft-deleted team should be deactivated");

        var groupRow = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == groupResourceId, ct);
        groupRow.IsActive.Should().BeTrue("the Group row must not be flipped by the DriveFolder pass");

        var fileRow = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == driveFileResourceId, ct);
        fileRow.IsActive.Should().BeTrue("the DriveFile row must not be flipped by the DriveFolder pass");
    }

    // ==========================================================================
    // 5. DriveFile pass reconciles its own rows (nobodies-collective/Humans#508)
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_DriveFilePass_RevokesAccessAndDeactivatesResource()
    {
        // Guard: SyncResourcesByTypeAsync(DriveFile, ...) must load rows via
        // IGoogleResourceRepository.GetActiveByResourceTypeAsync (the exact
        // requested type). A DriveFolder-only loader hard-filters at the DB
        // level, so the DriveFile pass would load zero rows and silently
        // no-op. GoogleResourceReconciliationJob runs exactly this pass every
        // tick, so soft-deleted teams' linked Drive *files* must have their
        // Google permissions revoked.
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        var googleId = CreateGoogleFolder("Doomed File");
        const string extraEmail = "ex-member@nobodies.team";

        await _drivePermissions.CreatePermissionAsync(googleId, extraEmail, "writer", ct);

        var resourceId = SeedDriveResource(teamId, googleId, "Doomed File", GoogleResourceType.DriveFile);
        await SaveAllAsync(ct);

        StubTeams((teamId, IsActive: false, Members: []));

        var result = await _syncService.SyncResourcesByTypeAsync(
            GoogleResourceType.DriveFile, SyncAction.Execute, ct);

        result.Diffs.Should().ContainSingle(d => d.ErrorMessage == null);

        var permissions = await _drivePermissions.ListPermissionsAsync(googleId, ct);
        permissions.Permissions.Should().BeEmpty("the extra permission on the DriveFile should have been revoked");

        var row = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
        row.IsActive.Should().BeFalse("the soft-deleted team's DriveFile resource should be deactivated for this type");
    }

    // ==========================================================================
    // 6. Sync-mode gating: AddOnly / None never deactivate
    // ==========================================================================

    [HumansTheory]
    [InlineData(SyncMode.AddOnly)]
    [InlineData(SyncMode.None)]
    public async Task SyncResourcesByTypeAsync_WhenNotAddAndRemove_DoesNotDeactivateOrRevoke(SyncMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(mode);

        var teamId = Guid.NewGuid();
        var googleId = CreateGoogleFolder("Doomed Folder");
        const string extraEmail = "ex-member@nobodies.team";
        await _drivePermissions.CreatePermissionAsync(googleId, extraEmail, "writer", ct);

        var resourceId = SeedDriveResource(teamId, googleId, "Doomed Folder");
        await SaveAllAsync(ct);

        StubTeams((teamId, IsActive: false, Members: []));

        await _syncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, ct);

        var row = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
        row.IsActive.Should().BeTrue("deactivation only happens when sync mode is AddAndRemove");

        var permissions = await _drivePermissions.ListPermissionsAsync(googleId, ct);
        permissions.Permissions.Should().ContainSingle(p => p.EmailAddress == extraEmail,
            "the extra permission must not be revoked outside AddAndRemove mode");
    }

    [HumansTheory]
    [InlineData(GoogleResourceType.DriveFolder)]
    [InlineData(GoogleResourceType.DriveFile)]
    public async Task SyncSingleResourceAsync_SharedDriveItem_PreservesOtherTeamsMemberAccess(
        GoogleResourceType resourceType)
    {
        var ct = TestContext.Current.CancellationToken;
        var firstTeam = Guid.NewGuid();
        var secondTeam = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        const string email = "second-team@nobodies.team";
        var googleId = CreateGoogleFolder("Shared Drive item");
        await _drivePermissions.CreatePermissionAsync(googleId, email, "writer", ct);
        var resourceId = SeedDriveResource(firstTeam, googleId, "First link", resourceType);
        SeedDriveResource(secondTeam, googleId, "Second link", resourceType);
        await SaveAllAsync(ct);
        StubTeams(
            (firstTeam, true, []),
            (secondTeam, true,
            [new TeamMemberInfo(Guid.NewGuid(), memberId, "Second-team human", email, null,
                TeamMemberRole.Member, Clock.GetCurrentInstant())]));
        // Teams' source unions both links' claims; the connector reconciles that union once.
        _driveSource.GetExpectedAccessAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, GoogleDriveAccessClaim>(StringComparer.Ordinal)
            {
                [googleId] = new(
                    new Dictionary<Guid, DrivePermissionLevel> { [memberId] = DrivePermissionLevel.Contributor },
                    [new TeamLink("First", "first"), new TeamLink("Second", "second")])
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [memberId] = new User { Id = memberId, DisplayName = "Second-team human", CreatedAt = Clock.GetCurrentInstant() }.ToUserInfo() with
                {
                    UserEmails = [new UserEmailInfo(Guid.NewGuid(), email, true, false, true, null, null, null, null,
                        Clock.GetCurrentInstant(), Clock.GetCurrentInstant(), LegacyGoogleEmailStatus.Unknown)]
                }
            });

        var diff = await _syncService.SyncSingleResourceAsync(resourceId, SyncAction.Execute, ct);

        diff.ErrorMessage.Should().BeNull();
        diff.Members.Should().ContainSingle().Which.State.Should().Be(MemberSyncState.Correct);
        diff.LinkedTeams.Should().HaveCount(2);
        var permissions = await _drivePermissions.ListPermissionsAsync(googleId, ct);
        permissions.Permissions.Should().ContainSingle(p => p.EmailAddress == email && p.Role == "writer");
        await _removalNotifications.DidNotReceiveWithAnyArgs().NotifyRemovalAsync(
            default!, default, default, default, default, default);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private string CreateGoogleFolder(string name) => _drivePermissions.SeedFolder(name);

    private Guid SeedDriveResource(
        Guid teamId,
        string googleId,
        string name,
        GoogleResourceType resourceType = GoogleResourceType.DriveFolder)
    {
        var id = Guid.NewGuid();
        GoogleIntegrationDb.GoogleResources.Add(new GoogleResource
        {
            Id = id,
            TeamId = teamId,
            ResourceType = resourceType,
            GoogleId = googleId,
            Name = name,
            DrivePermissionLevel = DrivePermissionLevel.Contributor,
            ProvisionedAt = Clock.GetCurrentInstant(),
            IsActive = true
        });
        return id;
    }

    private void StubTeams(params (Guid TeamId, bool IsActive, List<TeamMemberInfo> Members)[] teams)
    {
        var dict = teams.ToDictionary(
            t => t.TeamId,
            t => new TeamInfo(
                t.TeamId, $"Team {t.TeamId}", null, $"team-{t.TeamId:N}",
                IsActive: t.IsActive, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: t.Members));

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(dict);
    }
}
