using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Base.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;

// UserEmailMatch lives in the Profiles interface namespace, not DTOs.

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// Pins the three high-value invariants of <see cref="GoogleWorkspaceSyncService"/>
/// called out in the section-alignment Phase 2 audit:
/// <list type="number">
///   <item>All four gateway operations respect sync-mode — None means no Google call.</item>
///   <item>HTTP 403 from Google during an Add sets <c>GoogleEmailStatus = Rejected</c>;
///         transient errors (5xx) do not.</item>
/// </list>
/// Email-change status reset (Invariant 3) lives in <see cref="GoogleAdminService"/>
/// (GoogleIntegration-owned, not Users/Profiles) and is covered in
/// <see cref="GoogleAdminServiceTests"/> — see the test
/// <c>LinkAccountAsync_WhenLinkingNewEmail_ResetsGoogleEmailStatusToUnknown</c>.
/// </summary>
public sealed class GoogleWorkspaceSyncServiceTests
{
    // ── Shared collaborator fakes ──────────────────────────────────────────────

    private readonly IGoogleGroupProvisioningClient _groupProvisioning =
        Substitute.For<IGoogleGroupProvisioningClient>();

    private readonly IGoogleGroupSync _googleGroupSync =
        Substitute.For<IGoogleGroupSync>();

    private readonly IGoogleDrivePermissionsClient _drivePermissions =
        Substitute.For<IGoogleDrivePermissionsClient>();

    private readonly IGoogleDirectoryClient _directory =
        Substitute.For<IGoogleDirectoryClient>();

    private readonly ITeamResourceGoogleClient _teamResourceClient =
        Substitute.For<ITeamResourceGoogleClient>();

    private readonly IGoogleResourceRepository _resourceRepository =
        Substitute.For<IGoogleResourceRepository>();

    private readonly IGoogleSyncOutboxRepository _googleSyncOutboxRepository =
        Substitute.For<IGoogleSyncOutboxRepository>();

    private readonly ITeamServiceRead _teamService =
        Substitute.For<ITeamServiceRead>();

    private readonly IUserService _userService =
        Substitute.For<IUserService>();

    private readonly IUserEmailService _userEmailService =
        Substitute.For<IUserEmailService>();

    private readonly IAuditLogService _auditLogService =
        Substitute.For<IAuditLogService>();

    private readonly ISyncSettingsService _syncSettingsService =
        Substitute.For<ISyncSettingsService>();

    private readonly IGoogleRemovalNotificationService _removalNotifications =
        Substitute.For<IGoogleRemovalNotificationService>();

    private readonly GoogleWorkspaceSyncService _syncService;

    // ── Fixed test data ────────────────────────────────────────────────────────

    private static readonly Guid TestDriveFolderResourceId = Guid.Parse("aaaaaaaa-0002-0000-0000-000000000000");
    private static readonly Guid TestTeamId = Guid.Parse("bbbbbbbb-0001-0000-0000-000000000000");
    private static readonly Guid TestUserId = Guid.Parse("cccccccc-0001-0000-0000-000000000000");

    private const string TestGoogleFolderId = "01drivegoogleid";
    private const string TestUserEmail = "alice@nobodies.team";

    public GoogleWorkspaceSyncServiceTests()
    {
        // Safe defaults for audit / service-account helpers that every code
        // path may call even when the test doesn't care about them.
        _teamResourceClient
            .GetServiceAccountEmailAsync(Arg.Any<CancellationToken>())
            .Returns("sa@nobodies.team");

        var options = Options.Create(new GoogleWorkspaceOptions { Domain = "nobodies.team" });
        var clock = new FakeClock(Instant.FromUtc(2026, 5, 12, 10, 0));
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        _syncService = new GoogleWorkspaceSyncService(
            _groupProvisioning,
            _drivePermissions,
            _directory,
            _teamResourceClient,
            _resourceRepository,
            _googleSyncOutboxRepository,
            _teamService,
            _userService,
            _userEmailService,
            _googleGroupSync,
            _auditLogService,
            _syncSettingsService,
            _removalNotifications,
            options,
            clock,
            serviceProvider,
            NullLogger<GoogleWorkspaceSyncService>.Instance);
    }

    // ==========================================================================
    // Invariant 1 — Gateway-mode gating
    // ==========================================================================

    // Group-membership gateway tests removed by PR #478 (issue #615): per-user
    // AddUserToGroupAsync / RemoveUserFromGroupAsync gateways were retired in
    // favor of IGoogleGroupSync full-group reconciliation. Sync-mode gating and
    // 403→GoogleEmailStatus.Rejected behavior are now pinned by
    // GoogleGroupSyncServiceTests.

    [HumansFact]
    public async Task AddUserToDriveAsync_WhenSyncModeIsNone_DoesNotCallGoogle()
    {
        // AddUserToDriveAsync is private; we reach it through AddUserToTeamResourcesAsync.
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var user = MakeUser(TestUserId, TestUserEmail);
        _userService.GetUserInfoAsync(TestUserId, Arg.Any<CancellationToken>()).Returns(user);

        _userEmailService
            .GetEntitiesByUserIdAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailRowSnapshot(
                    Guid.NewGuid(),
                    TestUserId,
                    TestUserEmail,
                    IsVerified: true,
                    Provider: null,
                    ProviderKey: null,
                    IsGoogle: true,
                    IsPrimary: false,
                    Visibility: null,
                    VerificationSentAt: null,
                    CreatedAt: default,
                    UpdatedAt: default)
            ]);

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);
        _resourceRepository
            .GetActiveByTeamIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        // No parent team — prevents the subteam rollup from needing additional setup.
        _teamService.GetTeamAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.DidNotReceive()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveUserFromDriveAsync_WhenSyncModeIsNotAddAndRemove_DoesNotCallGoogle()
    {
        // RemoveUserFromDriveAsync is private; we reach it through SyncSingleResourceAsync
        // with SyncAction.Execute where there is an extra member to remove.
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddOnly); // not AddAndRemove → removal should be skipped

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);

        _resourceRepository
            .GetByIdAsync(TestDriveFolderResourceId, Arg.Any<CancellationToken>())
            .Returns(driveResource);
        _resourceRepository
            .GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        // TeamInfo cache resolves the team cross-section. No expected members
        // (empty team) — any permission in Google is "extra".
        var teamInfo = new TeamInfo(
            TestTeamId, "Test Team", null, "test-team",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: []);
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [TestTeamId] = teamInfo });

        _userEmailService
            .GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>());

        // Google Drive reports one direct user permission for an extra email.
        const string extraEmail = "extra@example.com";
        const string extraPermissionId = "perm-001";
        _drivePermissions
            .ListPermissionsAsync(TestGoogleFolderId, Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionListResult(
                Permissions: [
                    new DrivePermission(
                        Id: extraPermissionId,
                        Type: "user",
                        Role: "writer",
                        EmailAddress: extraEmail,
                        HasInheritedComponent: false)
                ],
                Error: null));

        _userEmailService
            .MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _syncService.SyncSingleResourceAsync(TestDriveFolderResourceId, SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        // Mode is AddOnly, so the delete gateway must not have been called.
        await _drivePermissions.DidNotReceive()
            .DeletePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Invariant 2 (permanent vs transient error classification) for Group
    // membership writes is now pinned by GoogleGroupSyncServiceTests; the
    // per-user AddUserToGroupAsync / RemoveUserFromGroupAsync gateways were
    // retired by PR #478 (issue #615).

    [HumansFact]
    public async Task AddUserToDriveAsync_When400NoGoogleAccount_MarksGoogleEmailRejected()
    {
        // Issue nobodies-collective/Humans#677 — Drive's permissions.create
        // returns HTTP 400 referencing SendNotificationEmail when the
        // recipient is not on a Google domain. Must mark the owning user's
        // GoogleEmailStatus as Rejected so the orchestrator stops retrying.
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var user = MakeUser(TestUserId, TestUserEmail);
        _userService.GetUserInfoAsync(TestUserId, Arg.Any<CancellationToken>()).Returns(user);
        _userService.GetByEmailOrAlternateAsync(TestUserEmail, Arg.Any<CancellationToken>())
            .Returns(MakeUser(TestUserId, TestUserEmail));

        _userEmailService
            .GetEntitiesByUserIdAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailRowSnapshot(
                    Guid.NewGuid(),
                    TestUserId,
                    TestUserEmail,
                    IsVerified: true,
                    Provider: null,
                    ProviderKey: null,
                    IsGoogle: true,
                    IsPrimary: false,
                    Visibility: null,
                    VerificationSentAt: null,
                    CreatedAt: default,
                    UpdatedAt: default)
            ]);

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);
        _resourceRepository
            .GetActiveByTeamIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        _teamService.GetTeamAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _drivePermissions
            .CreatePermissionAsync(TestGoogleFolderId, TestUserEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                new GoogleClientError(
                    400,
                    "The recipient has no Google account associated with this address. " +
                    "Please set SendNotificationEmail to true to invite them.")));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId, Xunit.TestContext.Current.CancellationToken);

        await _userService.Received(1).TrySetGoogleEmailStatusFromSyncAsync(
            TestUserId,
            GoogleEmailStatus.Rejected,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddUserToDriveAsync_WhenGeneric400_DoesNotMarkGoogleEmailRejected()
    {
        // Issue nobodies-collective/Humans#677 — generic 400 (malformed role,
        // etc.) is NOT a target-rejection. The orchestrator must continue
        // retrying these, so GoogleEmailStatus must not flip.
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var user = MakeUser(TestUserId, TestUserEmail);
        _userService.GetUserInfoAsync(TestUserId, Arg.Any<CancellationToken>()).Returns(user);

        _userEmailService
            .GetEntitiesByUserIdAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailRowSnapshot(
                    Guid.NewGuid(),
                    TestUserId,
                    TestUserEmail,
                    IsVerified: true,
                    Provider: null,
                    ProviderKey: null,
                    IsGoogle: true,
                    IsPrimary: false,
                    Visibility: null,
                    VerificationSentAt: null,
                    CreatedAt: default,
                    UpdatedAt: default)
            ]);

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);
        _resourceRepository
            .GetActiveByTeamIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        _teamService.GetTeamAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _drivePermissions
            .CreatePermissionAsync(TestGoogleFolderId, TestUserEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                new GoogleClientError(400, "Bad Request: invalid role 'archivist'")));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId, Xunit.TestContext.Current.CancellationToken);

        await _userService.DidNotReceiveWithAnyArgs()
            .TrySetGoogleEmailStatusFromSyncAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddUserToDriveAsync_WhenPreconditionCheckFailed_DoesNotMarkGoogleEmailRejected()
    {
        // Issue nobodies-collective/Humans#677 — Drive returns HTTP 400
        // "precondition check failed" for admin-configured policies like
        // sharing-outside-domain restrictions on a shared drive, NOT just for
        // missing Google accounts. The Cloud Identity Group path treats that
        // phrase as a target-rejection, but the Drive path must not — flipping
        // GoogleEmailStatus to Rejected would permanently silence retries for
        // what is actually an admin-configuration issue affecting every user.
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var user = MakeUser(TestUserId, TestUserEmail);
        _userService.GetUserInfoAsync(TestUserId, Arg.Any<CancellationToken>()).Returns(user);

        _userEmailService
            .GetEntitiesByUserIdAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailRowSnapshot(
                    Guid.NewGuid(),
                    TestUserId,
                    TestUserEmail,
                    IsVerified: true,
                    Provider: null,
                    ProviderKey: null,
                    IsGoogle: true,
                    IsPrimary: false,
                    Visibility: null,
                    VerificationSentAt: null,
                    CreatedAt: default,
                    UpdatedAt: default)
            ]);

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);
        _resourceRepository
            .GetActiveByTeamIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        _teamService.GetTeamAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _drivePermissions
            .CreatePermissionAsync(TestGoogleFolderId, TestUserEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                new GoogleClientError(
                    400,
                    "Precondition check failed for shared drive sharing policy.")));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId, Xunit.TestContext.Current.CancellationToken);

        await _userService.DidNotReceiveWithAnyArgs()
            .TrySetGoogleEmailStatusFromSyncAsync(Guid.Empty, default, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Issue nobodies-collective/Humans#945 — plus-address normalization (grant)
    // and upfront inherited-permission exclusion (delete)
    // ==========================================================================

    [HumansFact]
    public async Task AddUserToDriveAsync_WhenTargetIsPlusAddressedGmail_GrantsToCanonicalAddress()
    {
        // Drive's permissions.create rejects a plus-addressed local part with
        // an opaque HTTP 400. Plus-addressing is a guaranteed alias only on
        // Gmail, so the base address must be used for the API call.
        const string plusAddressedEmail = "alice+travel@gmail.com";
        const string canonicalEmail = "alice@gmail.com";

        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var user = MakeUser(TestUserId, plusAddressedEmail);
        _userService.GetUserInfoAsync(TestUserId, Arg.Any<CancellationToken>()).Returns(user);

        _userEmailService
            .GetEntitiesByUserIdAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailRowSnapshot(
                    Guid.NewGuid(),
                    TestUserId,
                    plusAddressedEmail,
                    IsVerified: true,
                    Provider: null,
                    ProviderKey: null,
                    IsGoogle: true,
                    IsPrimary: false,
                    Visibility: null,
                    VerificationSentAt: null,
                    CreatedAt: default,
                    UpdatedAt: default)
            ]);

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);
        _resourceRepository
            .GetActiveByTeamIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        _teamService.GetTeamAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _drivePermissions
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(DrivePermissionCreateOutcome.Created, Error: null));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.Received(1).CreatePermissionAsync(
            TestGoogleFolderId, canonicalEmail, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _drivePermissions.DidNotReceive().CreatePermissionAsync(
            TestGoogleFolderId, plusAddressedEmail, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveExtraDriveAccessAsync_WhenPermissionHasInheritedComponent_ExcludesFromRemovalUpfront()
    {
        // Per Peter's review of the original #945 fix: a permission that
        // still 403s on delete because it carries an inherited component must
        // never be selected for deletion in the first place — detect it
        // upfront (IsDirectManagedPermission / HasInheritedComponent) instead
        // of attempting the delete and persisting the failure afterward.
        const string extraEmail = "extra@example.com";
        const string extraPermissionId = "perm-001";

        SetupExtraDriveMemberScenario(extraEmail, extraPermissionId, hasInheritedComponent: true);

        await _syncService.SyncSingleResourceAsync(TestDriveFolderResourceId, SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.DidNotReceive()
            .DeletePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveUserFromDriveAsync_WhenInheritedPermission403_LogsDefensivelyWithoutThrowing()
    {
        // Defensive fallback only: the upfront exclusion normally prevents
        // this outcome, but a race (inheritance changed between list and
        // delete) could still surface it. Must not throw, and must not
        // persist anything (the persisted terminal-failure table was removed
        // per Peter's review — detection is upfront, not recorded state).
        const string extraEmail = "extra@example.com";
        const string extraPermissionId = "perm-001";

        SetupExtraDriveMemberScenario(extraEmail, extraPermissionId, hasInheritedComponent: false);

        _drivePermissions
            .DeletePermissionAsync(TestGoogleFolderId, extraPermissionId, Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionDeleteResult(
                DrivePermissionDeleteOutcome.InheritedPermission,
                new GoogleClientError(403,
                    "The authenticated user cannot delete the permission. If the permission is inherited, " +
                    "limited access must be leveraged.")));

        var act = async () => await _syncService.SyncSingleResourceAsync(
            TestDriveFolderResourceId, SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task DriveReconciliation_WhenMemberIsPlusAddressedGmail_RoundTripsToCorrectState()
    {
        // BLOCK review comment on PR 1147 — AddUserToDriveAsync grants to the
        // canonicalized Gmail address, but Drive's permissions.list always
        // returns that same canonical form. Expected-member keys built from
        // the member's raw plus-tagged stored email must be canonicalized the
        // same way, or the member never resolves to Correct and thrashes
        // grant/revoke every night.
        const string plusAddressedEmail = "alice+travel@gmail.com";
        const string canonicalEmail = "alice@gmail.com";

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);
        _resourceRepository
            .GetByIdAsync(TestDriveFolderResourceId, Arg.Any<CancellationToken>())
            .Returns(driveResource);
        _resourceRepository
            .GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        var member = new TeamMemberInfo(
            Guid.NewGuid(), TestUserId, "Alice Test", plusAddressedEmail, null,
            TeamMemberRole.Member, Instant.MinValue, GoogleEmailStatus.Valid);
        var teamInfo = new TeamInfo(
            TestTeamId, "Test Team", null, "test-team",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: [member]);
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [TestTeamId] = teamInfo });

        _userEmailService
            .GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>
            {
                [TestUserId] =
                [
                    new UserEmailRowSnapshot(
                        Guid.NewGuid(), TestUserId, plusAddressedEmail,
                        IsVerified: true, Provider: null, ProviderKey: null, IsGoogle: true,
                        IsPrimary: false, Visibility: null, VerificationSentAt: null,
                        CreatedAt: default, UpdatedAt: default)
                ]
            });

        // Drive reports the permission it actually granted — the
        // canonicalized address, matching what AddUserToDriveAsync targets.
        _drivePermissions
            .ListPermissionsAsync(TestGoogleFolderId, Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionListResult(
                Permissions: [
                    new DrivePermission(
                        Id: "perm-canonical",
                        Type: "user",
                        Role: "writer",
                        EmailAddress: canonicalEmail,
                        HasInheritedComponent: false)
                ],
                Error: null));

        var diff = await _syncService.SyncSingleResourceAsync(
            TestDriveFolderResourceId, SyncAction.Preview, Xunit.TestContext.Current.CancellationToken);

        var memberStatus = diff.Members.Should().ContainSingle().Subject;
        memberStatus.State.Should().Be(MemberSyncState.Correct,
            because: "the canonical Drive permission must match the plus-addressed member's expected key, not read as Missing/Extra");
    }

    private void SetupExtraDriveMemberScenario(string extraEmail, string extraPermissionId, bool hasInheritedComponent)
    {
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);

        var driveResource = MakeDriveFolderResource(TestDriveFolderResourceId, TestTeamId, TestGoogleFolderId);

        _resourceRepository
            .GetByIdAsync(TestDriveFolderResourceId, Arg.Any<CancellationToken>())
            .Returns(driveResource);
        _resourceRepository
            .GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>())
            .Returns([driveResource]);

        var teamInfo = new TeamInfo(
            TestTeamId, "Test Team", null, "test-team",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: []);
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [TestTeamId] = teamInfo });

        _userEmailService
            .GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>());

        _drivePermissions
            .ListPermissionsAsync(TestGoogleFolderId, Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionListResult(
                Permissions: [
                    new DrivePermission(
                        Id: extraPermissionId,
                        Type: "user",
                        Role: "writer",
                        EmailAddress: extraEmail,
                        HasInheritedComponent: hasInheritedComponent)
                ],
                Error: null));

        _userEmailService
            .MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static GoogleResource MakeDriveFolderResource(Guid id, Guid teamId, string googleId) =>
        new()
        {
            Id = id,
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFolder,
            GoogleId = googleId,
            Name = "Test Folder",
            DrivePermissionLevel = DrivePermissionLevel.Contributor,
            IsActive = true
        };

    private static UserInfo MakeUser(Guid userId, string email) =>
        UserInfo.Create(
            new User
            {
                Id = userId,
                UserName = $"user-{userId:N}",
                DisplayName = "Alice Test",
                Email = email
            },
            [], [], [], null, [], [], [], []);
}
