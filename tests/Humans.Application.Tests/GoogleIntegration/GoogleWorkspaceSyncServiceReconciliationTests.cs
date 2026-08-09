using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.GoogleIntegration;

/// <summary>
/// End-to-end regression coverage for the soft-deleted-team revocation path in
/// <see cref="GoogleWorkspaceSyncService.SyncResourcesByTypeAsync"/>, per
/// nobodies-collective/Humans#508. PR #227 (sprint/20260415/batch-3) layered
/// several fixes onto this flow and every bug was caught by review rather
/// than by a failing test — this file seeds a real (InMemory) <c>HumansDbContext</c>
/// through the real <see cref="GoogleResourceRepository"/> and a real
/// <see cref="TeamResourceService"/> (resolved via the service-locator seam
/// exactly as production does), and drives Drive reconciliation against the
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
public sealed class GoogleWorkspaceSyncServiceReconciliationTests : ServiceTestHarness
{
    private readonly StubGoogleDrivePermissionsClient _drivePermissions =
        new(NullLogger<StubGoogleDrivePermissionsClient>.Instance);

    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IGoogleRemovalNotificationService _removalNotifications = Substitute.For<IGoogleRemovalNotificationService>();
    private readonly ITeamResourceGoogleClient _teamResourceClient = Substitute.For<ITeamResourceGoogleClient>();

    private readonly GoogleWorkspaceSyncService _syncService;

    public GoogleWorkspaceSyncServiceReconciliationTests()
        : base(Instant.FromUtc(2026, 8, 2, 9, 0))
    {
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

        // Real TeamResourceService, resolved through the same service-locator seam
        // GoogleWorkspaceSyncService uses in production
        // (serviceProvider.GetRequiredService<ITeamResourceService>()), backed by
        // the same in-memory DbContext as the resource repository above — so
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

        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamResourceService>(teamResourceService)
            .Build();

        var options = Options.Create(new GoogleWorkspaceOptions { Domain = "nobodies.team" });

        _syncService = new GoogleWorkspaceSyncService(
            Substitute.For<IGoogleGroupProvisioningClient>(),
            _drivePermissions,
            Substitute.For<IGoogleDirectoryClient>(),
            _teamResourceClient,
            repository,
            Substitute.For<IGoogleSyncOutboxRepository>(),
            _teamService,
            _userService,
            _userEmailService,
            Substitute.For<IGoogleGroupSync>(),
            AuditLog,
            _syncSettingsService,
            _removalNotifications,
            options,
            Clock,
            serviceProvider,
            NullLogger<GoogleWorkspaceSyncService>.Instance);
    }

    // ==========================================================================
    // 1. Soft-deleted team revocation
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_SoftDeletedTeamWithExtraPermission_RevokesAccessAndDeactivatesResource()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        var googleId = await CreateGoogleFolderAsync("Doomed Folder");
        const string extraEmail = "ex-member@nobodies.team";

        // Simulate a permission that survived from when the team had members.
        await _drivePermissions.CreatePermissionAsync(googleId, extraEmail, "writer", ct);

        var resourceId = SeedDriveResource(teamId, googleId, "Doomed Folder");
        await SaveAllAsync(ct);

        // Team.IsActive = false + no active members (every TeamMember.LeftAt set).
        StubTeams((teamId, IsActive: false, Members: []));

        var result = await _syncService.SyncResourcesByTypeAsync(
            GoogleResourceType.DriveFolder, SyncAction.Execute, ct);

        result.Diffs.Should().ContainSingle(d => d.ErrorMessage == null);

        var permissions = await _drivePermissions.ListPermissionsAsync(googleId, ct);
        permissions.Permissions.Should().BeEmpty("the extra permission should have been revoked");

        var row = await GoogleIntegrationDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
        row.IsActive.Should().BeFalse("the soft-deleted team's Drive resource should be deactivated for this type");

        await _removalNotifications.Received(1).NotifyRemovalAsync(
            extraEmail, GoogleResourceType.DriveFolder, "Doomed Folder", Arg.Any<string?>(),
            Arg.Any<SyncRemovalReason>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // 2. Shared-GoogleId Drive groups: whole-group error blocks every row
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_SharedGoogleIdGroupErrors_NoRowInGroupIsDeactivated()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
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
        var ct = Xunit.TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();

        var cleanGoogleId = await CreateGoogleFolderAsync("Clean Folder");
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
        var ct = Xunit.TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();

        var googleId = await CreateGoogleFolderAsync("Folder");
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
    // 5. DriveFile pass reconciles its own rows (nobodies-collective/Humans#508
    //    follow-up — flagged by PR #1149 review comment 3700252152)
    // ==========================================================================

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_DriveFilePass_RevokesAccessAndDeactivatesResource()
    {
        // Regression for a live bug the type-ordering scenario above didn't
        // catch: SyncResourcesByTypeAsync(DriveFile, ...) used to call
        // IGoogleResourceRepository.GetActiveDriveFoldersAsync(), which
        // hard-filters to ResourceType == DriveFolder at the DB level — so the
        // DriveFile pass always loaded zero rows and silently no-opped.
        // GoogleResourceReconciliationJob runs exactly this pass every tick,
        // so soft-deleted teams' linked Drive *files* never had their Google
        // permissions revoked. Fixed by GetActiveByResourceTypeAsync (fetch by
        // the exact requested type instead of overfetching DriveFolder rows).
        var ct = Xunit.TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        var googleId = await CreateGoogleFolderAsync("Doomed File");
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
        var ct = Xunit.TestContext.Current.CancellationToken;
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(mode);

        var teamId = Guid.NewGuid();
        var googleId = await CreateGoogleFolderAsync("Doomed Folder");
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

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<string> CreateGoogleFolderAsync(string name)
    {
        var result = await _drivePermissions.CreateFolderAsync(name, parentFolderId: null, CancellationToken.None);
        result.Folder.Should().NotBeNull();
        return result.Folder!.Id!;
    }

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
