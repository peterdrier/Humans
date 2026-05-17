using Humans.Application.Configuration;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
// UserEmailMatch lives in the Profiles interface namespace, not DTOs.

namespace Humans.Application.Tests.GoogleIntegration;

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

    private readonly ITeamService _teamService =
        Substitute.For<ITeamService>();

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

    private static readonly Guid TestGroupResourceId = Guid.Parse("aaaaaaaa-0001-0000-0000-000000000000");
    private static readonly Guid TestDriveFolderResourceId = Guid.Parse("aaaaaaaa-0002-0000-0000-000000000000");
    private static readonly Guid TestTeamId = Guid.Parse("bbbbbbbb-0001-0000-0000-000000000000");
    private static readonly Guid TestUserId = Guid.Parse("cccccccc-0001-0000-0000-000000000000");

    private const string TestGoogleGroupId = "01groupgoogleid";
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
            .Returns((IReadOnlyList<UserEmailRowSnapshot>)[
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
        _teamService.GetTeamByIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((Team?)null);
        _teamService.GetUserTeamsAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([]);

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId);

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
                        IsInheritedOnly: false)
                ],
                Error: null));

        _userEmailService
            .MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _syncService.SyncSingleResourceAsync(TestDriveFolderResourceId, SyncAction.Execute);

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
            .Returns(new User
            {
                Id = TestUserId,
                UserName = $"user-{TestUserId:N}",
                DisplayName = "Alice Test",
                Email = TestUserEmail,
                GoogleEmailStatus = GoogleEmailStatus.Unknown
            });

        _userEmailService
            .GetEntitiesByUserIdAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserEmailRowSnapshot>)[
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

        _teamService.GetTeamByIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((Team?)null);
        _teamService.GetUserTeamsAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([]);

        _drivePermissions
            .CreatePermissionAsync(TestGoogleFolderId, TestUserEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                new GoogleClientError(
                    400,
                    "The recipient has no Google account associated with this address. " +
                    "Please set SendNotificationEmail to true to invite them.")));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId);

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
            .Returns((IReadOnlyList<UserEmailRowSnapshot>)[
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

        _teamService.GetTeamByIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((Team?)null);
        _teamService.GetUserTeamsAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([]);

        _drivePermissions
            .CreatePermissionAsync(TestGoogleFolderId, TestUserEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                new GoogleClientError(400, "Bad Request: invalid role 'archivist'")));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId);

        await _userService.DidNotReceiveWithAnyArgs()
            .TrySetGoogleEmailStatusFromSyncAsync(Guid.Empty, default, CancellationToken.None);
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
            .Returns((IReadOnlyList<UserEmailRowSnapshot>)[
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

        _teamService.GetTeamByIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns((Team?)null);
        _teamService.GetUserTeamsAsync(TestUserId, Arg.Any<CancellationToken>())
            .Returns([]);

        _drivePermissions
            .CreatePermissionAsync(TestGoogleFolderId, TestUserEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                new GoogleClientError(
                    400,
                    "Precondition check failed for shared drive sharing policy.")));

        await _syncService.AddUserToTeamResourcesAsync(TestTeamId, TestUserId);

        await _userService.DidNotReceiveWithAnyArgs()
            .TrySetGoogleEmailStatusFromSyncAsync(Guid.Empty, default, CancellationToken.None);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private void StageGroupResource()
    {
        _resourceRepository
            .GetByIdAsync(TestGroupResourceId, Arg.Any<CancellationToken>())
            .Returns(new GoogleResource
            {
                Id = TestGroupResourceId,
                TeamId = TestTeamId,
                ResourceType = GoogleResourceType.Group,
                GoogleId = TestGoogleGroupId,
                Name = "Test Group",
                IsActive = true
            });
    }

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
                Email = email,
                GoogleEmailStatus = GoogleEmailStatus.Unknown
            },
            [], [], [], null, [], [], [], []);
}
