using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Infrastructure.GoogleIntegration;

public sealed class GoogleGroupSyncServiceTests
{
    private readonly IGoogleGroupMembershipClient _membershipClient = Substitute.For<IGoogleGroupMembershipClient>();
    private readonly IGoogleGroupProvisioningClient _provisioningClient = Substitute.For<IGoogleGroupProvisioningClient>();
    private readonly ITeamResourceGoogleClient _teamResourceClient = Substitute.For<ITeamResourceGoogleClient>();
    private readonly ITeamResourceService _teamResourceService = Substitute.For<ITeamResourceService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly Dictionary<Guid, Profile> _profilesByUserId = new();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IGoogleRemovalNotificationService _removalNotifications = Substitute.For<IGoogleRemovalNotificationService>();
    private readonly RecordingGoogleGroupSyncScheduler _syncScheduler = new();
    private readonly RecordingLogger<GoogleGroupSyncService> _logger = new();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 10, 12, 0));

    public GoogleGroupSyncServiceTests()
    {
        _teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _teamResourceClient.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>())
            .Returns("service-account@nobodies.team");
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_ScopedHappyPath_DiffsAndAppliesMembership()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", alice, bob));
        StubUsers(
            (alice, "Alice", "alice@nobodies.team"),
            (bob, "Bob", "bob@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("alice@nobodies.team", "groups/group-1/memberships/alice"),
            new GroupMembership("old@nobodies.team", "groups/group-1/memberships/old"));

        _membershipClient.CreateMembershipAsync("group-1", "bob@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Added, null));
        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.MembersToAdd.Should().ContainSingle().Which.Should().Be("bob@nobodies.team");
        diff.MembersToRemove.Should().ContainSingle().Which.Should().Be("old@nobodies.team");
        await _membershipClient.Received(1)
            .CreateMembershipAsync("group-1", "bob@nobodies.team", Arg.Any<CancellationToken>());
        await _membershipClient.Received(1)
            .DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>());
        await _userService.Received(1)
            .GetUserInfosAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GoogleFailure_SchedulesScopedRetry()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        _provisioningClient.LookupGroupIdAsync("team@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult("group-1", null));
        _membershipClient.ListMembershipsAsync("group-1", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(null, new GoogleClientError(503, "backend error")));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().Contain("backend error");
        var scheduled = _syncScheduler.Scheduled.Should().ContainSingle().Which;
        scheduled.GroupKey.Should().Be("team@nobodies.team");
        scheduled.RetryAttempt.Should().Be(1);
        await _auditLogService.Received(1).LogAsync(
            AuditAction.GoogleSyncRetryScheduled,
            nameof(GoogleResource),
            Guid.Empty,
            Arg.Is<string>(s => s.Contains("Scheduled retry")),
            nameof(GoogleGroupSyncService));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GoogleFailure_AtRetryCap_DoesNotScheduleScopedRetry()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        _provisioningClient.LookupGroupIdAsync("team@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult("group-1", null));
        _membershipClient.ListMembershipsAsync("group-1", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(null, new GoogleClientError(503, "backend error")));

        var diff = await service.ReconcileOneAsync(
            "team@nobodies.team",
            SyncAction.Execute,
            CancellationToken.None,
            retryAttempt: 5);

        diff.ErrorMessage.Should().Contain("backend error");
        _syncScheduler.Scheduled.Should().BeEmpty();
        await _auditLogService.DidNotReceive().LogAsync(
            AuditAction.GoogleSyncRetryScheduled,
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [HumansFact]
    public async Task RequestSyncAsync_EmptyGroupKey_LogsWarningAndSkips()
    {
        var service = CreateService();

        await service.RequestSyncAsync("  ");

        _syncScheduler.Enqueued.Should().BeEmpty();
        _logger.Messages.Should().Contain(m =>
            m.Contains("RequestSyncAsync called with null/empty group key", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task ReconcileAllAsync_GoogleFailure_LogsWarningAndSchedulesScopedRetry()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        _provisioningClient.LookupGroupIdAsync("team@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult(null, new GoogleClientError(503, "backend error")));

        var result = await service.ReconcileAllAsync(SyncAction.Execute);

        result.ErrorCount.Should().Be(1);
        _logger.Messages.Should().Contain(m =>
            m.Contains("Google group sync failed", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal)
            && m.Contains("backend error", StringComparison.Ordinal));
        var scheduled = _syncScheduler.Scheduled.Should().ContainSingle().Which;
        scheduled.GroupKey.Should().Be("team@nobodies.team");
        scheduled.RetryAttempt.Should().Be(1);
    }

    [HumansFact]
    public async Task ReconcileAllAsync_AddFailure_LogsWarningWithMemberEmailAndSchedulesScopedRetry()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");
        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(503, "backend error")));

        var result = await service.ReconcileAllAsync(SyncAction.Execute);

        result.ErrorCount.Should().Be(1);
        _logger.Messages.Should().Contain(m =>
            m.Contains("Google group sync failed", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal)
            && m.Contains("alice@nobodies.team", StringComparison.Ordinal)
            && m.Contains("backend error", StringComparison.Ordinal));
        var scheduled = _syncScheduler.Scheduled.Should().ContainSingle().Which;
        scheduled.GroupKey.Should().Be("team@nobodies.team");
        scheduled.RetryAttempt.Should().Be(1);
    }

    [HumansFact]
    public async Task ReconcileAllAsync_RemoveFailure_LogsWarningWithMemberEmailAndSchedulesScopedRetry()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("old@nobodies.team", "groups/group-1/memberships/old"));
        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(503, "backend error"));

        var result = await service.ReconcileAllAsync(SyncAction.Execute);

        result.ErrorCount.Should().Be(1);
        _logger.Messages.Should().Contain(m =>
            m.Contains("Google group sync failed", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal)
            && m.Contains("old@nobodies.team", StringComparison.Ordinal)
            && m.Contains("backend error", StringComparison.Ordinal));
        var scheduled = _syncScheduler.Scheduled.Should().ContainSingle().Which;
        scheduled.GroupKey.Should().Be("team@nobodies.team");
        scheduled.RetryAttempt.Should().Be(1);
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UnclaimedGroup_RecordsResourceError()
    {
        var service = CreateService();
        var resource = StageResource("team@nobodies.team");

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ResourceId.Should().Be(resource.Id);
        diff.ErrorMessage.Should().Be("No Google group membership source claims this group");
        _logger.Messages.Should().Contain(m =>
            m.Contains("no source claims group key", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal));
        await _teamResourceService.Received(1).RecordResourceErrorAsync(
            resource.Id,
            "No Google group membership source claims this group",
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_ExecuteWithSyncModeNone_DoesNotMarkResourceSynced()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        var resource = StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1");
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.None);

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().BeNull();
        await _teamResourceService.DidNotReceive().MarkResourceSyncedAsync(
            resource.Id,
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_ResourceWithoutGroupUrl_MapsByOwningTeamGoogleEmail()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        var resource = StageResource("team@nobodies.team", includeGroupUrl: false);
        StubGroup("team@nobodies.team", "group-1");

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ResourceId.Should().Be(resource.Id);
        await _teamResourceService.Received(1).MarkResourceSyncedAsync(
            resource.Id,
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_DuplicateGroupResources_LogsWarning()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageDuplicateGroupResources("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1");

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Preview);

        _logger.Messages.Should().Contain(m =>
            m.Contains("Multiple active Google group resource rows", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal));
    }


    [HumansFact]
    public async Task ReconcileAllAsync_HydratesUsersOnceForWholePass()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var service = CreateService(
            new StaticSource("one@nobodies.team", alice),
            new StaticSource("two@nobodies.team", bob));
        StubUsers(
            (alice, "Alice", "alice@nobodies.team"),
            (bob, "Bob", "bob@nobodies.team"));
        StubGroup("one@nobodies.team", "group-1");
        StubGroup("two@nobodies.team", "group-2");

        var result = await service.ReconcileAllAsync(SyncAction.Preview);

        result.Diffs.Should().HaveCount(2);
        await _userService.Received(1)
            .GetUserInfosAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileAllAsync_CollisionIsAuditedAndSkipped()
    {
        var service = CreateService(
            new StaticSource("team@nobodies.team", Guid.NewGuid()),
            new StaticSource("team@nobodies.team", Guid.NewGuid()));

        var result = await service.ReconcileAllAsync(SyncAction.Execute);

        result.ErrorCount.Should().Be(1);
        await _provisioningClient.DidNotReceiveWithAnyArgs()
            .LookupGroupIdAsync(null!, CancellationToken.None);
        await _auditLogService.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            Guid.Empty,
            Arg.Is<string>(s => s.Contains("collision")),
            nameof(GoogleGroupSyncService));
    }

    [HumansFact]
    public async Task ReconcileOneAsync_Preview_UsesBurnerNameForExpectedMemberDisplay()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Legal Name", "alice@nobodies.team"));
        StubProfiles((userId, "Burner Name"));
        StubGroup("team@nobodies.team", "group-1");

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Preview);

        diff.Members.Should().ContainSingle().Which.DisplayName.Should().Be("Burner Name");
    }

    [HumansFact]
    public async Task ReconcileOneAsync_ServiceAccountMember_IsNotRemoved()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("service-account@nobodies.team", "groups/group-1/memberships/sa"));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.MembersToRemove.Should().BeEmpty();
        await _membershipClient.DidNotReceiveWithAnyArgs()
            .DeleteMembershipAsync(null!, CancellationToken.None);
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UserRejectedByGoogle_MarksGoogleEmailRejected()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");

        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(403, "member does not have a Google account")));
        _userService.GetByEmailOrAlternateAsync("alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = userId,
                DisplayName = "Alice",
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
                CreatedAt = _clock.GetCurrentInstant()
            }.ToUserInfo());

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().Contain("Google rejected alice@nobodies.team");
        _logger.Messages.Should().Contain(m =>
            m.Contains("alice@nobodies.team", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal)
            && m.Contains("User.GoogleEmailStatus", StringComparison.Ordinal));
        await _userService.Received(1).TrySetGoogleEmailStatusFromSyncAsync(
            userId,
            GoogleEmailStatus.Rejected,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UserRejectedByGoogle400Precondition_MarksGoogleEmailRejected()
    {
        // Issue nobodies-collective/Humans#677 — Cloud Identity returns HTTP
        // 400 / Error(4002) "Membership cannot be created since precondition
        // check failed" when the target email has no Google identity. Must
        // be treated as a permanent target rejection, same as the 403 path.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@external.test"));
        StubGroup("team@nobodies.team", "group-1");

        _membershipClient.CreateMembershipAsync("group-1", "alice@external.test", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(400, "Error(4002): Membership cannot be created since precondition check failed.")));
        _userService.GetByEmailOrAlternateAsync("alice@external.test", Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = userId,
                DisplayName = "Alice",
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
                CreatedAt = _clock.GetCurrentInstant()
            }.ToUserInfo());

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().Contain("Google rejected alice@external.test");
        diff.ErrorMessage.Should().Contain("HTTP 400");
        await _userService.Received(1).TrySetGoogleEmailStatusFromSyncAsync(
            userId,
            GoogleEmailStatus.Rejected,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GenericGoogle400_DoesNotMarkGoogleEmailRejected()
    {
        // Issue nobodies-collective/Humans#677 — make sure the 400 branch only
        // marks Rejected when the message text matches a target-rejection
        // pattern. A generic 400 (e.g. malformed payload) must not flip the
        // user's GoogleEmailStatus.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");

        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(400, "Bad Request: invalid argument 'roles'")));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().Contain("invalid argument 'roles'");
        await _userService.DidNotReceiveWithAnyArgs()
            .TrySetGoogleEmailStatusFromSyncAsync(Guid.Empty, default, CancellationToken.None);
        await _userService.DidNotReceiveWithAnyArgs()
            .GetByEmailOrAlternateAsync(null!, CancellationToken.None);
        _syncScheduler.Scheduled.Should().ContainSingle();
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GenericGoogle403_DoesNotMarkGoogleEmailRejected()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");

        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(403, "billing account disabled for project")));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().Contain("billing account disabled");
        await _userService.DidNotReceiveWithAnyArgs()
            .TrySetGoogleEmailStatusFromSyncAsync(Guid.Empty, default, CancellationToken.None);
        await _userService.DidNotReceiveWithAnyArgs()
            .GetByEmailOrAlternateAsync(null!, CancellationToken.None);
        _syncScheduler.Scheduled.Should().ContainSingle();
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GroupNotFound_AutoProvisionsAndReconciles()
    {
        // Issue nobodies-collective/Humans#740: when a membership source claims
        // a group that doesn't exist in Cloud Identity (HTTP 404), the
        // orchestrator auto-provisions it via CreateGroupAsync then re-runs
        // the lookup to proceed with the reconcile pass in the same call.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("new-group@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));

        // First lookup misses (404), then succeeds after create.
        _provisioningClient.LookupGroupIdAsync("new-group@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(
                new GroupLookupIdResult(null, new GoogleClientError(404, "not found")),
                new GroupLookupIdResult("group-new", null));
        _provisioningClient.CreateGroupAsync(
                "new-group@nobodies.team",
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new GroupCreateResult("group-new", null));
        _membershipClient.ListMembershipsAsync("group-new", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult([], null));
        _membershipClient.CreateMembershipAsync("group-new", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Added, null));

        var diff = await service.ReconcileOneAsync("new-group@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().BeNull();
        await _provisioningClient.Received(1).CreateGroupAsync(
            "new-group@nobodies.team",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _provisioningClient.Received(2).LookupGroupIdAsync(
            "new-group@nobodies.team", Arg.Any<CancellationToken>());
        await _membershipClient.Received(1).CreateMembershipAsync(
            "group-new", "alice@nobodies.team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GroupLookup403_AutoProvisionsAndReconciles()
    {
        // Cloud Identity sometimes returns HTTP 403 (instead of 404) when the
        // target group does not exist, because Google won't confirm/deny the
        // existence of a group the caller can't see. The orchestrator must
        // treat that as "not found" and attempt auto-provision.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("new-group@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));

        _provisioningClient.LookupGroupIdAsync("new-group@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(
                new GroupLookupIdResult(null, new GoogleClientError(403, "permission denied")),
                new GroupLookupIdResult("group-new", null));
        _provisioningClient.CreateGroupAsync(
                "new-group@nobodies.team",
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new GroupCreateResult("group-new", null));
        _membershipClient.ListMembershipsAsync("group-new", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult([], null));
        _membershipClient.CreateMembershipAsync("group-new", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Added, null));

        var diff = await service.ReconcileOneAsync("new-group@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().BeNull();
        await _provisioningClient.Received(1).CreateGroupAsync(
            "new-group@nobodies.team",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _provisioningClient.Received(2).LookupGroupIdAsync(
            "new-group@nobodies.team", Arg.Any<CancellationToken>());
        await _membershipClient.Received(1).CreateMembershipAsync(
            "group-new", "alice@nobodies.team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GroupNotFoundButCreateFails_RecordsErrorAndSchedulesRetry()
    {
        // When auto-provisioning fails (e.g. backend 503), the reconcile path
        // must fall through to the existing error/retry behavior — not silently
        // succeed and not crash.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("new-group@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));

        _provisioningClient.LookupGroupIdAsync("new-group@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult(null, new GoogleClientError(404, "not found")));
        _provisioningClient.CreateGroupAsync(
                "new-group@nobodies.team",
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new GroupCreateResult(null, new GoogleClientError(503, "backend error")));

        var diff = await service.ReconcileOneAsync("new-group@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().NotBeNull();
        _syncScheduler.Scheduled.Should().ContainSingle()
            .Which.GroupKey.Should().Be("new-group@nobodies.team");
    }

    [HumansFact]
    public async Task ReconcileOneAsync_RemoveFailure_AuditsRevokedFailure()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("old@nobodies.team", "groups/group-1/memberships/old"));

        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(503, "backend error"));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute);

        diff.ErrorMessage.Should().Contain("backend error");
        await _auditLogService.Received(1).LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessRevoked,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            nameof(GoogleGroupSyncService),
            "old@nobodies.team",
            "MEMBER",
            GoogleSyncSource.ScheduledSync,
            success: false,
            errorMessage: Arg.Is<string>(s => s.Contains("backend error")),
            relatedEntityId: null,
            relatedEntityType: null);
    }

    private GoogleGroupSyncService CreateService(params IGoogleGroupMembershipSource[] sources) => new(
        sources,
        _membershipClient,
        _provisioningClient,
        _teamResourceClient,
        _teamResourceService,
        _teamService,
        _userService,
        _userEmailService,
        _syncSettingsService,
        _auditLogService,
        _removalNotifications,
        _syncScheduler,
        Options.Create(new GoogleWorkspaceOptions()),
        _clock,
        _logger);

    private void StubUsers(params (Guid UserId, string DisplayName, string Email)[] users)
    {
        var userEntities = users.ToDictionary(
            u => u.UserId,
            u => new User
            {
                Id = u.UserId,
                DisplayName = u.DisplayName,
                CreatedAt = _clock.GetCurrentInstant()
            });
        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                return userEntities
                    .Where(kv => requested.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                IReadOnlyDictionary<Guid, UserInfo> dict = userEntities
                    .Where(kv => requested.Contains(kv.Key))
                    .ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ToUserInfo(profile: _profilesByUserId.GetValueOrDefault(kv.Key)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        _userEmailService.GetEntitiesByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                return users
                    .Where(u => requested.Contains(u.UserId))
                    .ToDictionary(
                        u => u.UserId,
                        u => (IReadOnlyList<UserEmailRowSnapshot>)
                        [
                            new UserEmailRowSnapshot(
                                Guid.NewGuid(),
                                u.UserId,
                                u.Email,
                                IsVerified: true,
                                Provider: null,
                                ProviderKey: null,
                                IsGoogle: true,
                                IsPrimary: false,
                                Visibility: null,
                                VerificationSentAt: null,
                                CreatedAt: _clock.GetCurrentInstant(),
                                UpdatedAt: _clock.GetCurrentInstant())
                        ]);
            });

        foreach (var u in users)
        {
            _profilesByUserId[u.UserId] = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = u.UserId,
                State = ProfileState.Active,
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            };
        }
    }

    private void StubProfiles(params (Guid UserId, string? BurnerName)[] profiles)
    {
        foreach (var p in profiles)
        {
            _profilesByUserId[p.UserId] = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = p.UserId,
                BurnerName = p.BurnerName ?? string.Empty,
                State = ProfileState.Active,
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            };
        }
    }

    private void StubGroup(string groupKey, string groupId, params GroupMembership[] currentMembers)
    {
        _provisioningClient.LookupGroupIdAsync(groupKey, Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult(groupId, null));
        _membershipClient.ListMembershipsAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(currentMembers, null));
    }

    private GoogleResource StageResource(string groupEmail, bool includeGroupUrl = true)
    {
        var teamId = Guid.NewGuid();
        var parts = groupEmail.Split('@');
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = "group-1",
            Name = "Team Group",
            Url = includeGroupUrl ? $"https://groups.google.com/a/{parts[1]}/g/{parts[0]}" : null,
            IsActive = true
        };

        _teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [teamId] = 1 });
        _teamResourceService.GetResourcesByTeamIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(teamId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>
            {
                [teamId] =
                [
                    ToSnapshot(resource)
                ]
            });
        _teamService.GetTeamByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = teamId,
                Name = "Team",
                Slug = "team",
                GoogleGroupPrefix = parts[0],
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            });

        return resource;
    }

    private void StageDuplicateGroupResources(string groupEmail)
    {
        var first = StageResource(groupEmail);
        var secondTeamId = Guid.NewGuid();
        var parts = groupEmail.Split('@');
        var second = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = secondTeamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = "group-2",
            Name = "Duplicate Team Group",
            Url = $"https://groups.google.com/a/{parts[1]}/g/{parts[0]}",
            IsActive = true
        };

        _teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>
            {
                [first.TeamId] = 1,
                [secondTeamId] = 1
            });
        _teamResourceService.GetResourcesByTeamIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(first.TeamId) && ids.Contains(secondTeamId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>
            {
                [first.TeamId] = [ToSnapshot(first)],
                [secondTeamId] = [ToSnapshot(second)]
            });
        _teamService.GetTeamByIdAsync(secondTeamId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = secondTeamId,
                Name = "Duplicate Team",
                Slug = "duplicate-team",
                GoogleGroupPrefix = parts[0],
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            });
    }

    private static GoogleResourceSnapshot ToSnapshot(GoogleResource resource) =>
        new(
            resource.Id,
            resource.TeamId,
            resource.GoogleId,
            resource.Name,
            resource.ResourceType,
            resource.Url,
            IsActive: resource.IsActive);

    private sealed class StaticSource(string key, params Guid[] userIds) : IGoogleGroupMembershipSource
    {
        public Task<Dictionary<string, Guid[]>> GetExpectedAsync(
            string? groupKey = null,
            CancellationToken ct = default)
        {
            if (groupKey is not null && !string.Equals(groupKey, key, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase));

            return Task.FromResult(new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = userIds
            });
        }
    }

    private sealed class RecordingGoogleGroupSyncScheduler : IGoogleGroupSyncScheduler
    {
        public List<string> Enqueued { get; } = [];
        public List<(string GroupKey, TimeSpan Delay, int RetryAttempt)> Scheduled { get; } = [];

        public void Enqueue(string groupKey) => Enqueued.Add(groupKey);

        public void Schedule(string groupKey, TimeSpan delay, int retryAttempt) =>
            Scheduled.Add((groupKey, delay, retryAttempt));
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
