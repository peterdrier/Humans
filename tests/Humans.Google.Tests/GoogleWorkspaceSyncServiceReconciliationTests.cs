using Humans.Google.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Google.Services;
using Humans.Base.Enums;
using Humans.Google.Data;
using Humans.Google.Services.Workspace;
using Humans.Google.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Google.Tests;

/// <summary>
/// Seeds a real InMemory context through the real repository and TeamResourceService, and drives the
/// Drive engine against the dev stub and group-link restoration against the resource repository.
/// Group membership reconciliation is covered by <c>GoogleGroupSyncServiceTests</c>.
/// </summary>
public sealed class GoogleWorkspaceSyncServiceReconciliationTests : GoogleTestHarness
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
    private readonly IGoogleGroupSync _googleGroupSync = Substitute.For<IGoogleGroupSync>();

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
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());

        // Default: full Drive reconciliation (add + remove) enabled.
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);

        IGoogleResourceRepository repository = new GoogleResourceRepository(GoogleDbFactory);

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
            .Returns(call => Task.FromResult(GoogleDb.GoogleResources.Local
                .Where(r => r.IsActive && r.ResourceType != GoogleResourceType.Group
                    && (call.ArgAt<string?>(0) is null || string.Equals(r.GoogleId, call.ArgAt<string?>(0), StringComparison.Ordinal)))
                .Select(r => r.GoogleId).Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => new GoogleDriveAccessClaim([]), StringComparer.Ordinal)));
        var driveSync = new GoogleDriveAccessSyncService(
            [_driveSource], _drivePermissions, _userService, _identities, _userEmailService, _syncSettingsService,
            _syncLog, Substitute.For<IGoogleSyncLogViewer>(), _removalNotifications, repository, _teamService, teamResourceService,
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
            _googleGroupSync,
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

    [HumansTheory]
    [InlineData("target@nobodies.team", false)]
    [InlineData("12345", false)]
    [InlineData("target@nobodies.team", true)]
    [InlineData("12345", true)]
    public async Task EnsureTeamGroupAsync_ReactivatesMatchingInactiveGroupWithoutReplacingManualGroups(
        string inactiveGoogleId,
        bool confirmReactivation)
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        const string teamName = "Current team name";
        const string prefix = "target";
        const string expectedUrl = "https://groups.google.com/a/nobodies.team/g/target";
        var now = Clock.GetCurrentInstant();
        var selectedOldId = SeedGroupResource(teamId, "old-group-id", "Old group", "https://groups.google.com/a/nobodies.team/g/old", now - Duration.FromDays(2));
        var otherManualId = SeedGroupResource(teamId, "manual-group-id", "Manual group", "https://groups.google.com/a/nobodies.team/g/manual", now - Duration.FromDays(1));
        var inactiveId = SeedGroupResource(teamId, inactiveGoogleId, "Stale inactive name", expectedUrl, now - Duration.FromDays(3), isActive: false);
        await SaveAllAsync(ct);

        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(new TeamInfo(
            teamId, teamName, null, "current-team", true, false, SystemTeamType.None, false,
            false, false, false, Instant.MinValue, [], GoogleGroupPrefix: prefix));
        _googleGroupSync.ReconcileOneAsync(Arg.Any<string>(), SyncAction.Execute, Arg.Any<CancellationToken>(),
                scheduleRetries: false, syncSource: GoogleSyncSource.ManualSync)
            .Returns(new ResourceSyncDiff { GoogleId = "12345" });

        var result = await _syncService.EnsureTeamGroupAsync(teamId, confirmReactivation, ct);

        var rows = await GoogleDb.GoogleResources.AsNoTracking().Where(r => r.TeamId == teamId).ToListAsync(ct);
        rows.Should().HaveCount(3);
        rows.Single(r => r.Id == otherManualId).IsActive.Should().BeTrue("manually linked groups remain active");

        if (!confirmReactivation)
        {
            result.RequiresConfirmation.Should().BeTrue();
            result.InactiveResourceId.Should().Be(inactiveId);
            rows.Single(r => r.Id == selectedOldId).IsActive.Should().BeTrue();
            rows.Single(r => r.Id == inactiveId).IsActive.Should().BeFalse();
            await _googleGroupSync.DidNotReceiveWithAnyArgs().ReconcileOneAsync(default!, default, default);
            AuditLog.ReceivedCalls().Should().BeEmpty();
            return;
        }

        result.Success.Should().BeTrue();
        var reactivated = rows.Single(r => r.Id == inactiveId);
        reactivated.IsActive.Should().BeTrue();
        reactivated.Name.Should().Be(teamName);
        reactivated.Url.Should().Be(expectedUrl);
        rows.Single(r => r.Id == selectedOldId).IsActive.Should().BeFalse("only the selected oldest existing group is replaced");

        await _googleGroupSync.DidNotReceiveWithAnyArgs().ReconcileOneAsync(default!, default, default);
        await AuditLog.Received(1).LogAsync(
            GoogleAuditActions.GoogleResourceDeactivated, TeamGoogleResource.AuditEntityType, selectedOldId,
            Arg.Any<string>(), nameof(GoogleWorkspaceSyncService), teamId, "Team");
        await AuditLog.Received(1).LogAsync(
            GoogleAuditActions.GoogleResourceProvisioned, TeamGoogleResource.AuditEntityType, inactiveId,
            Arg.Any<string>(), nameof(GoogleWorkspaceSyncService), teamId, "Team");
    }

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

        var row = await GoogleDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
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

        var rows = await GoogleDb.GoogleResources.AsNoTracking()
            .Where(r => r.Id == resourceId1 || r.Id == resourceId2)
            .ToListAsync(ct);
        rows.Should().OnlyContain(r => r.IsActive, "no row in an errored GoogleId group may be deactivated");
        rows.Should().OnlyContain(r => r.ErrorMessage != null);
    }

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

        var rows = await GoogleDb.GoogleResources.AsNoTracking()
            .Where(r => r.Id == cleanResourceId || r.Id == erroredResourceId)
            .ToListAsync(ct);
        rows.Should().OnlyContain(r => r.IsActive,
            "DeactivateResourcesForTeamAsync(team, type) is all-or-nothing; one errored resource must block the clean one too");
    }

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_DriveFolderPass_DoesNotDeactivateGroupOrDriveFileRowsForSameTeam()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();

        var googleId = CreateGoogleFolder("Folder");
        var folderResourceId = SeedDriveResource(teamId, googleId, "Folder");
        var groupResourceId = SeedDriveResource(teamId, "group-id", "Group", GoogleResourceType.Group);
        var driveFileResourceId = SeedDriveResource(teamId, "file-id", "File", GoogleResourceType.DriveFile);
        await SaveAllAsync(ct);

        StubTeams((teamId, IsActive: false, Members: []));

        await _syncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, ct);

        var folderRow = await GoogleDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == folderResourceId, ct);
        folderRow.IsActive.Should().BeFalse("the DriveFolder row for the soft-deleted team should be deactivated");

        var groupRow = await GoogleDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == groupResourceId, ct);
        groupRow.IsActive.Should().BeTrue("the Group row must not be flipped by the DriveFolder pass");

        var fileRow = await GoogleDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == driveFileResourceId, ct);
        fileRow.IsActive.Should().BeTrue("the DriveFile row must not be flipped by the DriveFolder pass");
    }

    [HumansFact]
    public async Task SyncResourcesByTypeAsync_DriveFilePass_RevokesAccessAndDeactivatesResource()
    {
        // The DriveFile pass must load rows of exactly the requested type: a DriveFolder-only
        // loader filters at the DB, so this pass would load nothing and silently leave
        // soft-deleted teams' linked files shared.
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

        var row = await GoogleDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
        row.IsActive.Should().BeFalse("the soft-deleted team's DriveFile resource should be deactivated for this type");
    }

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

        var row = await GoogleDb.GoogleResources.AsNoTracking().SingleAsync(r => r.Id == resourceId, ct);
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
                    UserEmails = [new UserEmailInfo(Guid.NewGuid(), email, true, false, null, null, null, null,
                        Clock.GetCurrentInstant(), Clock.GetCurrentInstant())]
                }
            });

        var diff = await _syncService.SyncSingleResourceAsync(resourceId, SyncAction.Execute, ct);

        diff.ErrorMessage.Should().BeNull();
        diff.Members.Should().ContainSingle().Which.State.Should().Be(MemberSyncState.Correct);
        diff.LinkedTeams.Should().HaveCount(2);
        var permissions = await _drivePermissions.ListPermissionsAsync(googleId, ct);
        permissions.Permissions.Should().ContainSingle(p => p.EmailAddress == email && p.Role == "writer");
        await _removalNotifications.DidNotReceiveWithAnyArgs().NotifyRemovalAsync(
            default!, default, default, default, default);
    }

    private string CreateGoogleFolder(string name) => _drivePermissions.SeedFolder(name);

    private Guid SeedDriveResource(
        Guid teamId,
        string googleId,
        string name,
        GoogleResourceType resourceType = GoogleResourceType.DriveFolder)
    {
        var id = Guid.NewGuid();
        GoogleDb.GoogleResources.Add(new TeamGoogleResource
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

    private Guid SeedGroupResource(
        Guid teamId,
        string googleId,
        string name,
        string url,
        Instant provisionedAt,
        bool isActive = true)
    {
        var id = Guid.NewGuid();
        GoogleDb.GoogleResources.Add(new TeamGoogleResource
        {
            Id = id,
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = googleId,
            Name = name,
            Url = url,
            ProvisionedAt = provisionedAt,
            LastSyncedAt = provisionedAt,
            IsActive = isActive
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
