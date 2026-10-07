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

    [HumansTheory]
    [Xunit.InlineData("reader", DrivePermissionLevel.Contributor, "writer")]
    [Xunit.InlineData("writer", DrivePermissionLevel.Viewer, "reader")]
    public async Task ReconcileOneAsync_DirectLevelChange_UpdatesInPlace(
        string currentRole, DrivePermissionLevel expectedLevel, string expectedRole)
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, expectedLevel)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", new DrivePermission("perm-1", "user", currentRole, "alice@nobodies.team", HasInheritedComponent: false));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.Received(1)
            .UpdatePermissionAsync("folder-1", "perm-1", expectedRole, Arg.Any<CancellationToken>());
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .DeletePermissionAsync(default!, default!, default);
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(default!, default!, default!, default);
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
    public async Task ReconcileOneAsync_MergeTombstoneInTheAccessList_IsFilteredOut()
    {
        // #1704: the read resolves merges forward, so an ACL entry keyed to an archived id
        // answers with the living survivor and the MergedToUserId test never sees a tombstone.
        // The entry must still grant nobody: it names an id that is no longer a human, and
        // handing the survivor access off it would be a silent grant nothing asked for.
        var archived = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (archived, DrivePermissionLevel.Contributor)));
        StubUsers((survivor, "Survivor", "survivor@nobodies.team"));
        ResolveTo(archived, survivor);
        StubFolder("folder-1");

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.Members.Should().BeEmpty();
        await _drivePermissions.DidNotReceiveWithAnyArgs()
            .CreatePermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task ReconcileOneAsync_MixedWriterBecomesViewer_ReportsRoleDrift()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", MixedPermission("writer", "reader"));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Preview, Xunit.TestContext.Current.CancellationToken);

        var member = diff.Members.Should().ContainSingle().Subject;
        member.State.Should().Be(MemberSyncState.WrongRole);
        member.ExpectedRole.Should().Be("reader");
    }

    [HumansFact]
    public async Task ReconcileOneAsync_MixedWriterLeaves_ReportsDirectElevationToRemove()
    {
        var service = CreateService(new StaticSource("folder-1"));
        StubFolder("folder-1", MixedPermission("writer", "reader"));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Preview, Xunit.TestContext.Current.CancellationToken);

        var member = diff.Members.Should().ContainSingle().Subject;
        member.State.Should().Be(MemberSyncState.Extra);
        member.ExpectedRole.Should().Be("reader", "inherited access remains after the direct elevation is removed");
    }

    [HumansTheory]
    [Xunit.InlineData(false, "reader")]
    [Xunit.InlineData(true, "reader")]
    [Xunit.InlineData(false, "commenter")]
    [Xunit.InlineData(true, "commenter")]
    public async Task ReconcileOneAsync_MixedElevationRemoved_PreservesInheritedFloorAndSettles(
        bool departedOrRetired, string inheritedRole)
    {
        var alice = Guid.NewGuid();
        var service = CreateService(departedOrRetired
            ? new StaticSource("folder-1")
            : new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", MixedPermission("writer", "reader", inheritedRole));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked, Guid.Empty,
            Arg.Is<string>(text => text.Contains($"from writer to {inheritedRole}", StringComparison.Ordinal)),
            nameof(GoogleDriveAccessSyncService), "alice@nobodies.team", inheritedRole,
            GoogleSyncSource.ScheduledSync, success: true, errorMessage: null,
            userId: departedOrRetired ? null : alice, ct: Arg.Any<CancellationToken>());

        // Drive may retain equal direct/inherited components or return only inheritance.
        // Neither representation should trigger another update or an impossible delete.
        foreach (var permission in new[]
        {
            MixedPermission(inheritedRole, inheritedRole),
            new DrivePermission("perm-1", "user", inheritedRole, "alice@nobodies.team", HasInheritedComponent: true),
        })
        {
            StubFolder("folder-1", permission);
            var settled = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);
            settled.IsInSync.Should().BeTrue();
        }

        await _drivePermissions.Received(1)
            .UpdatePermissionAsync("folder-1", "perm-1", inheritedRole, Arg.Any<CancellationToken>());
        await _drivePermissions.DidNotReceiveWithAnyArgs().DeletePermissionAsync(default!, default!, default);
        await _drivePermissions.DidNotReceiveWithAnyArgs().CreatePermissionAsync(default!, default!, default!, default);
        await _removalNotifications.DidNotReceiveWithAnyArgs().NotifyRemovalAsync(
            default!, default, default, default!, default, default);
    }

    [HumansTheory]
    [Xunit.InlineData(SyncAction.Preview, SyncMode.AddAndRemove, false)]
    [Xunit.InlineData(SyncAction.Preview, SyncMode.AddAndRemove, true)]
    [Xunit.InlineData(SyncAction.Execute, SyncMode.None, false)]
    [Xunit.InlineData(SyncAction.Execute, SyncMode.None, true)]
    [Xunit.InlineData(SyncAction.Execute, SyncMode.AddOnly, false)]
    [Xunit.InlineData(SyncAction.Execute, SyncMode.AddOnly, true)]
    public async Task ReconcileOneAsync_MixedDowngrade_RespectsPreviewAndSyncMode(
        SyncAction action, SyncMode mode, bool departed)
    {
        var alice = Guid.NewGuid();
        var service = CreateService(departed
            ? new StaticSource("folder-1")
            : new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", MixedPermission("writer", "reader"));
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>()).Returns(mode);

        var diff = await service.ReconcileOneAsync("folder-1", action, Xunit.TestContext.Current.CancellationToken);

        diff.IsInSync.Should().BeFalse("the mode stops writes without hiding drift");
        _drivePermissions.ReceivedCalls().Should().ContainSingle().Which
            .GetMethodInfo().Name.Should().Be(nameof(IGoogleDrivePermissionsClient.ListPermissionsAsync));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_MixedElevation_AddOnlyUpdatesAndLogs()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.ContentManager)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", MixedPermission("writer", "reader"));
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, Arg.Any<CancellationToken>()).Returns(SyncMode.AddOnly);

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _drivePermissions.Received(1)
            .UpdatePermissionAsync("folder-1", "perm-1", "fileOrganizer", Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessGranted, Guid.Empty, Arg.Any<string>(),
            nameof(GoogleDriveAccessSyncService), "alice@nobodies.team", "fileOrganizer",
            GoogleSyncSource.ScheduledSync, success: true, errorMessage: null, userId: alice,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ReconcileOneAsync_MixedUpdateFails_LogsFailureWithoutRemovalNotice(bool departed)
    {
        var alice = Guid.NewGuid();
        var service = CreateService(departed
            ? new StaticSource("folder-1")
            : new StaticSource("folder-1", (alice, DrivePermissionLevel.Viewer)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", MixedPermission("writer", "reader"));
        _drivePermissions.UpdatePermissionAsync("folder-1", "perm-1", "reader", Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(403, "forbidden"));

        await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked, Guid.Empty, Arg.Any<string>(),
            nameof(GoogleDriveAccessSyncService), "alice@nobodies.team", "reader",
            GoogleSyncSource.ScheduledSync, success: false,
            errorMessage: Arg.Is<string>(text => text.Contains("403", StringComparison.Ordinal)),
            userId: departed ? null : alice, ct: Arg.Any<CancellationToken>());
        _googleSyncLog.ReceivedCalls().Should().ContainSingle();
        await _drivePermissions.DidNotReceiveWithAnyArgs().DeletePermissionAsync(default!, default!, default);
        await _drivePermissions.DidNotReceiveWithAnyArgs().CreatePermissionAsync(default!, default!, default!, default);
        await _removalNotifications.DidNotReceiveWithAnyArgs().NotifyRemovalAsync(
            default!, default, default, default!, default, default);
    }

    [HumansFact]
    public async Task ReconcileOneAsync_MixedWriterStillExpected_NoMutation()
    {
        var alice = Guid.NewGuid();
        var service = CreateService(new StaticSource("folder-1", (alice, DrivePermissionLevel.Contributor)));
        StubUsers((alice, "Alice", "alice@nobodies.team"));
        StubFolder("folder-1", MixedPermission("writer", "reader"));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.Members.Should().ContainSingle().Which.State.Should().Be(MemberSyncState.Correct);
        _drivePermissions.ReceivedCalls().Should().ContainSingle().Which
            .GetMethodInfo().Name.Should().Be(nameof(IGoogleDrivePermissionsClient.ListPermissionsAsync));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_InheritedOwnerAndServiceAccountExtras_AreUntouched()
    {
        var service = CreateService(new StaticSource("folder-1"));
        StubFolder("folder-1",
            new DrivePermission("inherited", "user", "writer", "inherited@nobodies.team", true),
            new DrivePermission("owner", "user", "owner", "owner@nobodies.team", false),
            new DrivePermission("service", "user", "writer", "bot@project.iam.gserviceaccount.com", false));

        var diff = await service.ReconcileOneAsync("folder-1", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.IsInSync.Should().BeTrue();
        _drivePermissions.ReceivedCalls().Should().ContainSingle().Which
            .GetMethodInfo().Name.Should().Be(nameof(IGoogleDrivePermissionsClient.ListPermissionsAsync));
    }

    // Use the actual connector mapping so dropping either permissionDetails component
    // reproduces the production failure, rather than testing an impossible DTO shape.
    private static DrivePermission MixedPermission(string directRole, params string[] inheritedRoles)
    {
        var permission = new Google.Apis.Drive.v3.Data.Permission
        {
            Id = "perm-1",
            Type = "user",
            Role = directRole,
            EmailAddress = "alice@nobodies.team",
            PermissionDetails =
            [
                .. inheritedRoles.Select(role => new Google.Apis.Drive.v3.Data.Permission.PermissionDetailsData
                    { Inherited = true, Role = role, PermissionType = "member" }),
                new() { Inherited = false, Role = directRole, PermissionType = "file" }
            ]
        };
        return (DrivePermission)typeof(GoogleDrivePermissionsClient)
            .GetMethod("MapPermission", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [permission])!;
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

    /// <summary>Answers <paramref name="requestedId"/> with the survivor's record and the
    /// survivor's addresses, the way Users resolves a merge tombstone forward (#1704).</summary>
    private void ResolveTo(Guid requestedId, Guid survivorId)
    {
        _usersById[requestedId] = _usersById[survivorId];
        _userEmailService.GetEntitiesByUserIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(requestedId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>
            {
                [requestedId] =
                [
                    new UserEmailRowSnapshot(
                        Guid.NewGuid(), requestedId, "survivor@nobodies.team", IsVerified: true,
                        Provider: null, ProviderKey: null, IsGoogle: true, IsPrimary: false,
                        Visibility: null, VerificationSentAt: null, CreatedAt: _now, UpdatedAt: _now)
                ]
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
