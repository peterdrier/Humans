using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Base.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleDriveAccessSyncServiceTests
{
    private readonly IGoogleDrivePermissionsClient _drivePermissions = Substitute.For<IGoogleDrivePermissionsClient>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IGoogleSyncLogService _googleSyncLog = Substitute.For<IGoogleSyncLogService>();
    private readonly IGoogleRemovalNotificationService _removalNotifications = Substitute.For<IGoogleRemovalNotificationService>();
    private readonly RecordingLogger<GoogleDriveAccessSyncService> _logger = new();
    private readonly Instant _now = Instant.FromUtc(2026, 9, 10, 12, 0);

    public GoogleDriveAccessSyncServiceTests()
    {
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                (IReadOnlyDictionary<Guid, UserInfo>)new Dictionary<Guid, UserInfo>()));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_MissingMember_GrantsAndLogs()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Contributor)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1");

        _drivePermissions.CreatePermissionAsync("folder-1", "alice@nobodies.team", "writer", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(DrivePermissionCreateOutcome.Created, null));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().ContainSingle().Which.Should().Be("alice@nobodies.team");
        await _drivePermissions.Received(1)
            .CreatePermissionAsync("folder-1", "alice@nobodies.team", "writer", Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessGranted, Guid.Empty, Arg.Any<string>(),
            nameof(GoogleDriveAccessSyncService), "alice@nobodies.team", "writer",
            GoogleSyncSource.ScheduledSync, success: true, errorMessage: null, userId: alice,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_ExtraMember_RemovesAndLogs()
    {
        var service = CreateService(new StaticSource("folder-1"));
        StubFolder("folder-1", new DrivePermission("perm-1", "user", "reader", "old@nobodies.team", HasInheritedComponent: false));

        _drivePermissions.DeletePermissionAsync("folder-1", "perm-1", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionDeleteResult(DrivePermissionDeleteOutcome.Deleted, null));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToRemove.Should().ContainSingle().Which.Should().Be("old@nobodies.team");
        await _drivePermissions.Received(1).DeletePermissionAsync("folder-1", "perm-1", Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked, Guid.Empty, Arg.Any<string>(),
            nameof(GoogleDriveAccessSyncService), "old@nobodies.team", "MEMBER",
            GoogleSyncSource.ScheduledSync, success: true, errorMessage: null, userId: null,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_LevelChange_RemovesOldGrantsNew()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Contributor)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", new DrivePermission("perm-1", "user", "reader", "alice@nobodies.team", HasInheritedComponent: false));

        _drivePermissions.DeletePermissionAsync("folder-1", "perm-1", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionDeleteResult(DrivePermissionDeleteOutcome.Deleted, null));
        _drivePermissions.CreatePermissionAsync("folder-1", "alice@nobodies.team", "writer", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(DrivePermissionCreateOutcome.Created, null));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.Received(1).DeletePermissionAsync("folder-1", "perm-1", Arg.Any<CancellationToken>());
        await _drivePermissions.Received(1)
            .CreatePermissionAsync("folder-1", "alice@nobodies.team", "writer", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_CorrectMember_NoOp()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Contributor)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", new DrivePermission("perm-1", "user", "writer", "alice@nobodies.team", HasInheritedComponent: false));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().BeEmpty();
        diff.MembersToRemove.Should().BeEmpty();
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .DeletePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileAllAsync_TwoSourcesClaimSameFolder_SkippedLoggedAudited()
    {
        var service = CreateService(
            new StaticSource("folder-1", (Guid.NewGuid(), DrivePermissionLevel.Viewer)),
            new StaticSource("folder-1", (Guid.NewGuid(), DrivePermissionLevel.Viewer)));

        var result = await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        result.ErrorCount.Should().Be(1);
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .ListPermissionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            GoogleResourceType.DriveFolder.ToString(),
            Guid.Empty,
            Arg.Is<string>(s => s.Contains("collision")),
            nameof(GoogleDriveAccessSyncService));
    }

    [HumansFact]
    public async Task ReconcileAllAsync_SourceThrows_OtherSourcesStillSync()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new ThrowingSource(), new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1");
        _drivePermissions.CreatePermissionAsync("folder-1", "alice@nobodies.team", "reader", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(DrivePermissionCreateOutcome.Created, null));

        var result = await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        result.Diffs.Should().ContainSingle();
        await _drivePermissions.Received(1)
            .CreatePermissionAsync("folder-1", "alice@nobodies.team", "reader", Arg.Any<CancellationToken>());
        _logger.Messages.Should().Contain(m => m.Contains("threw", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_SuspendedUser_IsFilteredOut()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        SuspendUser(alice);
        StubFolder("folder-1");

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.Members.Should().BeEmpty();
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UserWithNoUsableGoogleEmail_IsFilteredOut()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsersWithNoEmail(alice);
        StubFolder("folder-1");

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.Members.Should().BeEmpty();
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_SyncModeNone_ComputesDiffButDoesNotMutate()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1");
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().ContainSingle();
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_AddOnlyMode_DoesNotRemoveExtras()
    {
        var service = CreateService(new StaticSource("folder-1"));
        StubFolder("folder-1", new DrivePermission("perm-1", "user", "reader", "old@nobodies.team", HasInheritedComponent: false));
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddOnly);

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToRemove.Should().ContainSingle();
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .DeletePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_InheritedBelowExpectedLevel_GrantsADirectPermission()
    {
        // Workgroup folders sit under a root that hands every Colaborador an inherited Viewer;
        // a member who is one must still be raised to Contributor on the group's own folder.
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Contributor)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", new DrivePermission(
            "perm-root", "user", "reader", "alice@nobodies.team", HasInheritedComponent: true));
        _drivePermissions.CreatePermissionAsync("folder-1", "alice@nobodies.team", "writer", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionMutationResult(DrivePermissionCreateOutcome.Created, null));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.Received(1)
            .CreatePermissionAsync("folder-1", "alice@nobodies.team", "writer", Arg.Any<CancellationToken>());
        // The inherited permission is untouchable at this level (#945) — only the grant happens.
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .DeletePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_InheritedAtOrAboveExpectedLevel_IsLeftAlone()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", new DrivePermission(
            "perm-root", "user", "writer", "alice@nobodies.team", HasInheritedComponent: true));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .DeletePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_FailedRemoval_DoesNotNotifyTheFormerMember()
    {
        var service = CreateService(new StaticSource("folder-1"));
        StubFolder("folder-1", new DrivePermission("perm-1", "user", "reader", "old@nobodies.team", HasInheritedComponent: false));
        _drivePermissions.DeletePermissionAsync("folder-1", "perm-1", Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionDeleteResult(
                DrivePermissionDeleteOutcome.Failed, new GoogleClientError(500, "boom")));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _removalNotifications.DidNotReceiveWithAnyArgs().NotifyRemovalAsync(
            Arg.Any<string>(), Arg.Any<GoogleResourceType>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SyncRemovalReason>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UnclaimedFolder_ReturnsError()
    {
        var service = CreateService();

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Be("No Google Drive access source claims this folder");
    }

    private GoogleDriveAccessSyncService CreateService(params IGoogleDriveAccessSource[] sources) => new(
        sources,
        _drivePermissions,
        _userService,
        _userEmailService,
        _syncSettingsService,
        _auditLogService,
        _googleSyncLog,
        _removalNotifications,
        _logger);

    private readonly Dictionary<Guid, UserInfo> _usersById = new();

    private void StubUsers(params (Guid UserId, string DisplayName, string Email)[] users)
    {
        foreach (var u in users)
        {
            var user = new User { Id = u.UserId, DisplayName = u.DisplayName, CreatedAt = _now };
            _usersById[u.UserId] = user.ToUserInfo();
        }

        WireUserLookups(users.Select(u => (u.UserId, u.Email)).ToArray());
    }

    private void StubUsersWithNoEmail(Guid userId)
    {
        var user = new User { Id = userId, DisplayName = "NoEmail", CreatedAt = _now };
        _usersById[userId] = user.ToUserInfo();

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                IReadOnlyDictionary<Guid, UserInfo> dict = _usersById
                    .Where(kv => requested.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        _userEmailService.GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>
            {
                [userId] = []
            });
    }

    private void SuspendUser(Guid userId)
    {
        _usersById[userId] = _usersById[userId] with { State = UserState.Suspended };
    }

    private void WireUserLookups((Guid UserId, string Email)[] emails)
    {
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                IReadOnlyDictionary<Guid, UserInfo> dict = _usersById
                    .Where(kv => requested.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        _userEmailService.GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                return emails
                    .Where(e => requested.Contains(e.UserId))
                    .ToDictionary(
                        e => e.UserId,
                        e => (IReadOnlyList<UserEmailRowSnapshot>)
                        [
                            new UserEmailRowSnapshot(
                                Guid.NewGuid(), e.UserId, e.Email, IsVerified: true, Provider: null,
                                ProviderKey: null, IsGoogle: true, IsPrimary: false, Visibility: null,
                                VerificationSentAt: null, CreatedAt: _now, UpdatedAt: _now)
                        ]);
            });
    }

    private void StubFolder(string folderId, params DrivePermission[] currentPermissions)
    {
        _drivePermissions.ListPermissionsAsync(folderId, Arg.Any<CancellationToken>())
            .Returns(new DrivePermissionListResult(currentPermissions, null));
    }

    private sealed class StaticSource(string folderId, params (Guid UserId, DrivePermissionLevel Level)[] access)
        : IGoogleDriveAccessSource
    {
        public Task<Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>> GetExpectedAccessAsync(
            string? folderId2 = null,
            CancellationToken ct = default)
        {
            if (folderId2 is not null && !string.Equals(folderId2, folderId, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>(StringComparer.OrdinalIgnoreCase));

            return Task.FromResult(new Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>(StringComparer.OrdinalIgnoreCase)
            {
                [folderId] = access.ToDictionary(a => a.UserId, a => a.Level)
            });
        }
    }

    private sealed class ThrowingSource : IGoogleDriveAccessSource
    {
        public Task<Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>> GetExpectedAccessAsync(
            string? folderId = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
