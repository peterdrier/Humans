using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Base.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleGroupSyncServiceTests
{
    private readonly IGoogleGroupMembershipClient _membershipClient = Substitute.For<IGoogleGroupMembershipClient>();
    private readonly IGoogleGroupProvisioningClient _provisioningClient = Substitute.For<IGoogleGroupProvisioningClient>();
    private readonly ITeamResourceGoogleClient _teamResourceClient = Substitute.For<ITeamResourceGoogleClient>();
    private readonly ITeamResourceManagement _teamResourceService = Substitute.For<ITeamResourceManagement>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IWorkspaceIdentityManagement _identities;
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly Dictionary<Guid, ProfileInfo> _profilesByUserId = new();
    // The resolver reads only User.BurnerName, so StubProfiles must reach back and update the
    // already-built User, as the production CopyNamesToUser dual-write does.
    private readonly Dictionary<Guid, User> _usersByUserId = new();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IGoogleSyncLogService _googleSyncLog = Substitute.For<IGoogleSyncLogService>();
    private readonly IGoogleRemovalNotificationService _removalNotifications = Substitute.For<IGoogleRemovalNotificationService>();
    private readonly RecordingGoogleGroupSyncScheduler _syncScheduler = new();
    private readonly RecordingLogger<GoogleGroupSyncService> _logger = new();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 10, 12, 0));

    public GoogleGroupSyncServiceTests()
    {
        _identities = WorkspaceIdentityFakes.ResolvingThrough(_userService);
        _teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _teamResourceClient.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>())
            .Returns("service-account@nobodies.team");
        _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public async Task ReconcileAsync_ManualTriggerSurvivesLogsAndRetries(bool bulk, bool fails)
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        var resource = StageResource("team@nobodies.team");
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");
        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(fails ? GroupMembershipMutationOutcome.Failed : GroupMembershipMutationOutcome.Added,
                fails ? new GoogleClientError(503, "backend error") : null));

        if (bulk)
            await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken, GoogleSyncSource.ManualSync);
        else
            await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute,
                Xunit.TestContext.Current.CancellationToken, syncSource: GoogleSyncSource.ManualSync);

        await _googleSyncLog.Received(1).LogAsync(GoogleSyncLogAction.AccessGranted, resource.Id,
            Arg.Any<string>(), nameof(GoogleGroupSyncService), "alice@nobodies.team", "MEMBER",
            GoogleSyncSource.ManualSync, success: !fails, errorMessage: Arg.Any<string?>(),
            userId: userId, ct: Arg.Any<CancellationToken>());
        if (fails)
            _syncScheduler.Scheduled.Should().ContainSingle().Which.Source.Should().Be(GoogleSyncSource.ManualSync);
    }

    [HumansFact]
    public async Task RequestSyncAsync_PreservesTriggerWhenEnqueuing()
    {
        await CreateService().RequestSyncAsync(" team@nobodies.team ", Xunit.TestContext.Current.CancellationToken,
            GoogleSyncSource.TeamMemberJoined);

        _syncScheduler.Enqueued.Should().ContainSingle().Which
            .Should().Be(("team@nobodies.team", GoogleSyncSource.TeamMemberJoined));
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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().ContainSingle().Which.Should().Be("bob@nobodies.team");
        diff.MembersToRemove.Should().ContainSingle().Which.Should().Be("old@nobodies.team");
        await _membershipClient.Received(1)
            .CreateMembershipAsync("group-1", "bob@nobodies.team", Arg.Any<CancellationToken>());
        await _membershipClient.Received(1)
            .DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>());
        await _identities.Received(1)
            .GetWorkspaceIdentitiesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(GoogleSyncLogAction.AccessGranted, Guid.Empty,
            Arg.Is<string>(text => text.Contains("team@nobodies.team")), nameof(GoogleGroupSyncService),
            "bob@nobodies.team", "MEMBER", GoogleSyncSource.ScheduledSync, success: true,
            userId: bob, ct: Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(GoogleSyncLogAction.AccessRevoked, Guid.Empty,
            Arg.Is<string>(text => text.Contains("team@nobodies.team")), nameof(GoogleGroupSyncService),
            "old@nobodies.team", "MEMBER", GoogleSyncSource.ScheduledSync, success: true,
            userId: null, ct: Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData("a.l.i.c.e+burn@googlemail.com", "alice@gmail.com", true)]
    [Xunit.InlineData("a.l.i.c.e@example.org", "alice@example.org", false)]
    public async Task ReconcileOneAsync_GmailIdentityAliasesMatchWithoutConflatingOtherDomains(
        string selected, string vendorEmail, bool sameIdentity)
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", selected));
        StubGroup("team@nobodies.team", "group-1", new GroupMembership(vendorEmail, "membership-1"));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Preview,
            Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().HaveCount(sameIdentity ? 0 : 1);
        diff.MembersToRemove.Should().HaveCount(sameIdentity ? 0 : 1);
        if (sameIdentity)
            diff.Members.Should().ContainSingle().Which.State.Should().Be(MemberSyncState.Correct);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ReconcileOneAsync_AliasClaims_GrantOnceWithStableAttribution(bool reverseClaims)
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var service = CreateService(new StaticSource("team@nobodies.team",
            reverseClaims ? [second, first] : [first, second]));
        StubUsers((first, "First", "a.l.i.c.e+burn@gmail.com"), (second, "Second", "alice@googlemail.com"));
        StubGroup("team@nobodies.team", "group-1");
        _membershipClient.CreateMembershipAsync("group-1", "a.l.i.c.e@gmail.com", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Added, null));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute,
            Xunit.TestContext.Current.CancellationToken);

        diff.Members.Should().ContainSingle().Which.UserId.Should().Be(first);
        await _membershipClient.Received(1).CreateMembershipAsync("group-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GmailPlusTagMember_AddsCanonicalAddress()
    {
        // Google's Directory API rejects the "+tag" form of a Gmail address
        // (HTTP 404). The expected member email is canonicalized to the form
        // Google resolves it to before the add.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice+travel@gmail.com"));
        StubGroup("team@nobodies.team", "group-1");
        _membershipClient.CreateMembershipAsync("group-1", "alice@gmail.com", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Added, null));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().ContainSingle().Which.Should().Be("alice@gmail.com");
        await _membershipClient.Received(1)
            .CreateMembershipAsync("group-1", "alice@gmail.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GmailPlusTagMember_AlreadyPresentAsCanonical_NotReAdded()
    {
        // The member is already in the group under its canonical address while
        // the stored email keeps its "+tag". Matching must treat them as the
        // same member so the sync doesn't re-add (and re-remove) every pass.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice+travel@gmail.com"));
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("alice@gmail.com", "groups/group-1/memberships/alice"));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().BeEmpty();
        diff.MembersToRemove.Should().BeEmpty();
        await _membershipClient.DidNotReceiveWithAnyArgs()
            .CreateMembershipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _membershipClient.DidNotReceiveWithAnyArgs()
            .DeleteMembershipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("backend error");
        var scheduled = _syncScheduler.Scheduled.Should().ContainSingle().Which;
        scheduled.GroupKey.Should().Be("team@nobodies.team");
        scheduled.RetryAttempt.Should().Be(1);
        await _auditLogService.Received(1).LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
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
            Xunit.TestContext.Current.CancellationToken,
            retryAttempt: 5);

        diff.ErrorMessage.Should().Contain("backend error");
        _syncScheduler.Scheduled.Should().BeEmpty();
        await _auditLogService.DidNotReceive().LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [HumansFact]
    public async Task RequestSyncAsync_EmptyGroupKey_LogsWarningAndSkips()
    {
        var service = CreateService();

        await service.RequestSyncAsync("  ", Xunit.TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Preview, Xunit.TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAllAsync(SyncAction.Preview, Xunit.TestContext.Current.CancellationToken);

        result.Diffs.Should().HaveCount(2);
        await _identities.Received(1)
            .GetWorkspaceIdentitiesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileAllAsync_CollisionIsAuditedAndSkipped()
    {
        var service = CreateService(
            new StaticSource("team@nobodies.team", Guid.NewGuid()),
            new StaticSource("team@nobodies.team", Guid.NewGuid()));

        var result = await service.ReconcileAllAsync(SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        result.ErrorCount.Should().Be(1);
        await _provisioningClient.DidNotReceiveWithAnyArgs()
            .LookupGroupIdAsync(null!, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            GoogleIntegrationAuditActions.AnomalousPermissionDetected,
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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Preview, Xunit.TestContext.Current.CancellationToken);

        diff.Members.Should().ContainSingle().Which.DisplayName.Should().Be("Burner Name");
    }

    [HumansFact]
    public async Task ReconcileOneAsync_ServiceAccountMember_IsNotRemoved()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("service-account@nobodies.team", "groups/group-1/memberships/sa"));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToRemove.Should().BeEmpty();
        await _membershipClient.DidNotReceiveWithAnyArgs()
            .DeleteMembershipAsync(null!, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UnlinkedGroup_AddFailureStillLeavesSyncLog()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");
        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(503, "backend error")));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute,
            Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("backend error");
        await _googleSyncLog.Received(1).LogAsync(GoogleSyncLogAction.AccessGranted, Guid.Empty,
            Arg.Is<string>(text => text.Contains("team@nobodies.team")), nameof(GoogleGroupSyncService),
            "alice@nobodies.team", "MEMBER", GoogleSyncSource.ScheduledSync, success: false,
            errorMessage: Arg.Is<string>(text => text.Contains("backend error")), userId: userId,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_RejectionStatusWriteFails_RecordsGoogleFailureAndRetryFirst()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        var resource = StageResource("team@nobodies.team");
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");
        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(403, "member does not have a Google account")));
        var failure = new IOException("Email status write failed");
        _identities.SetStatusFromSyncAsync(userId, Arg.Any<string>(), GoogleEmailStatus.Rejected, Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(failure));

        Func<Task> reconcile = () => service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute,
            Xunit.TestContext.Current.CancellationToken);
        (await reconcile.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        await _teamResourceService.Received(1).RecordResourceErrorAsync(resource.Id,
            Arg.Is<string>(s => s.Contains("Google rejected alice@nobodies.team")), Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(GoogleSyncLogAction.AccessGranted, resource.Id,
            Arg.Any<string>(), nameof(GoogleGroupSyncService), "alice@nobodies.team", "MEMBER",
            GoogleSyncSource.ScheduledSync, success: false, errorMessage: Arg.Any<string>(),
            userId: userId, ct: Arg.Any<CancellationToken>());
        _syncScheduler.Scheduled.Should().ContainSingle().Which.GroupKey.Should().Be("team@nobodies.team");
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
                CreatedAt = _clock.GetCurrentInstant()
            }.ToUserInfo());

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("Google rejected alice@nobodies.team");
        _logger.Messages.Should().Contain(m =>
            m.Contains("alice@nobodies.team", StringComparison.Ordinal)
            && m.Contains("team@nobodies.team", StringComparison.Ordinal)
            && m.Contains("Google email status marked Rejected", StringComparison.Ordinal));
        await _identities.Received(1).SetStatusFromSyncAsync(
            userId,
            Arg.Any<string>(),
            GoogleEmailStatus.Rejected,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_UserRejectedByGoogle400Precondition_MarksGoogleEmailRejected()
    {
        // Cloud Identity returns 400 / Error(4002) "precondition check failed" when the target
        // email has no Google identity: a permanent target rejection, like the 403 path.
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
                CreatedAt = _clock.GetCurrentInstant()
            }.ToUserInfo());

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("Google rejected alice@external.test");
        diff.ErrorMessage.Should().Contain("HTTP 400");
        await _identities.Received(1).SetStatusFromSyncAsync(
            userId,
            Arg.Any<string>(),
            GoogleEmailStatus.Rejected,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GenericGoogle400_DoesNotMarkGoogleEmailRejected()
    {
        // The 400 branch marks Rejected only when the message matches a target-rejection
        // pattern; a generic 400 (e.g. malformed payload) must not flip GoogleEmailStatus.
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        StubUsers((userId, "Alice", "alice@nobodies.team"));
        StubGroup("team@nobodies.team", "group-1");

        _membershipClient.CreateMembershipAsync("group-1", "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(
                GroupMembershipMutationOutcome.Failed,
                new GoogleClientError(400, "Bad Request: invalid argument 'roles'")));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("invalid argument 'roles'");
        await _identities.DidNotReceiveWithAnyArgs()
            .SetStatusFromSyncAsync(Guid.Empty, Arg.Any<string>(), default, Arg.Any<CancellationToken>());
        await _userService.DidNotReceiveWithAnyArgs()
            .GetByEmailOrAlternateAsync(null!, Arg.Any<CancellationToken>());
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

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("billing account disabled");
        await _identities.DidNotReceiveWithAnyArgs()
            .SetStatusFromSyncAsync(Guid.Empty, Arg.Any<string>(), default, Arg.Any<CancellationToken>());
        await _userService.DidNotReceiveWithAnyArgs()
            .GetByEmailOrAlternateAsync(null!, Arg.Any<CancellationToken>());
        _syncScheduler.Scheduled.Should().ContainSingle();
    }

    [HumansFact]
    public async Task ReconcileOneAsync_GroupNotFound_AutoProvisionsAndReconciles()
    {
        // A membership source claiming a group missing from Cloud Identity (404) auto-provisions
        // it, then re-runs the lookup and reconciles in the same call.
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

        var diff = await service.ReconcileOneAsync("new-group@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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
        await _auditLogService.Received(1).LogAsync(GoogleIntegrationAuditActions.GoogleResourceProvisioned,
            nameof(GoogleResource), Guid.Empty, Arg.Is<string>(text => text.Contains("new-group@nobodies.team")),
            nameof(GoogleGroupSyncService));
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

        var diff = await service.ReconcileOneAsync("new-group@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

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

        var diff = await service.ReconcileOneAsync("new-group@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().NotBeNull();
        _syncScheduler.Scheduled.Should().ContainSingle()
            .Which.GroupKey.Should().Be("new-group@nobodies.team");
    }

    [HumansFact]
    public async Task ReconcileOneAsync_RemoveFailure_RecordsRevokedFailureInTheSyncLog()
    {
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("old@nobodies.team", "groups/group-1/memberships/old"));

        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(503, "backend error"));

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.ErrorMessage.Should().Contain("backend error");
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            nameof(GoogleGroupSyncService),
            "old@nobodies.team",
            "MEMBER",
            GoogleSyncSource.ScheduledSync,
            success: false,
            errorMessage: Arg.Is<string>(s => s.Contains("backend error")),
            userId: null,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_Grant_RecordsTheMembersUserIdInTheSyncLog()
    {
        var bob = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", bob));
        StubUsers((bob, "Bob", "bob@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1");

        _membershipClient.CreateMembershipAsync("group-1", "bob@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Added, null));

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        // Without the id the row is invisible to /Google/Human/{id} and to the GDPR export.
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessGranted,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            nameof(GoogleGroupSyncService),
            "bob@nobodies.team",
            "MEMBER",
            GoogleSyncSource.ScheduledSync,
            success: true,
            errorMessage: null,
            userId: bob,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_Revoke_ResolvesTheExtraMembersUserIdFromTheirEmail()
    {
        var departed = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("old@nobodies.team", "groups/group-1/memberships/old"));

        _userEmailService.MatchByEmailsAsync(
                Arg.Is<IReadOnlyCollection<string>>(e => e.Contains("old@nobodies.team")),
                Arg.Any<CancellationToken>())
            .Returns(new List<UserEmailMatch>
            {
                new("old@nobodies.team", departed, true, true, _clock.GetCurrentInstant())
            });
        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        // A human who left the team is only reachable from the address Google still holds.
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            nameof(GoogleGroupSyncService),
            "old@nobodies.team",
            "MEMBER",
            GoogleSyncSource.ScheduledSync,
            success: true,
            errorMessage: null,
            userId: departed,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_Revoke_PrefersTheVerifiedOwnerOverAnUnverifiedDuplicate()
    {
        var unverifiedSquatter = Guid.NewGuid();
        var verifiedOwner = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("old@nobodies.team", "groups/group-1/memberships/old"));

        // MatchByEmailsAsync has no ordering; the unverified row comes back first here.
        _userEmailService.MatchByEmailsAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserEmailMatch>
            {
                new("old@nobodies.team", unverifiedSquatter, false, false, _clock.GetCurrentInstant()),
                new("old@nobodies.team", verifiedOwner, true, true, _clock.GetCurrentInstant())
            });
        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        // Attributing the row to the squatter would leak it into the wrong GDPR export.
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            nameof(GoogleGroupSyncService),
            "old@nobodies.team",
            "MEMBER",
            GoogleSyncSource.ScheduledSync,
            success: true,
            errorMessage: null,
            userId: verifiedOwner,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_RejectedSignup_RemovesTheStillClaimedMembership()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", userId));
        var resource = StageResource("team@nobodies.team");
        StubUsers((userId, "Rejected member", "rejected@nobodies.team"));
        _usersByUserId[userId].State = UserState.Rejected;
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("rejected@nobodies.team", "groups/group-1/memberships/rejected"));
        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserEmailMatch>
            {
                new("rejected@nobodies.team", userId, true, true, _clock.GetCurrentInstant())
            });
        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/rejected", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _membershipClient.Received(1).DeleteMembershipAsync(
            "groups/group-1/memberships/rejected", Arg.Any<CancellationToken>());
        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked, resource.Id, Arg.Any<string>(), nameof(GoogleGroupSyncService),
            "rejected@nobodies.team", "MEMBER", GoogleSyncSource.ScheduledSync,
            success: true, errorMessage: null, userId: userId, ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_Revoke_ResolvesTheExtraMemberFromAGmailAliasRow()
    {
        // Google reports the canonical address, but the
        // only user_emails row is a dotted "+tag" alias of it. The alias match is worthless
        // unless the owner lookup is keyed by the same Gmail identity Google reports.
        var departed = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team"));
        StageResource("team@nobodies.team");
        StubGroup("team@nobodies.team", "group-1",
            new GroupMembership("olduser@gmail.com", "groups/group-1/memberships/old"));

        _userEmailService.MatchByEmailsAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserEmailMatch>
            {
                new("old.user+burn@googlemail.com", departed, true, true, _clock.GetCurrentInstant())
            });
        _membershipClient.DeleteMembershipAsync("groups/group-1/memberships/old", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);

        await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        await _googleSyncLog.Received(1).LogAsync(
            GoogleSyncLogAction.AccessRevoked,
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            nameof(GoogleGroupSyncService),
            "olduser@gmail.com",
            "MEMBER",
            GoogleSyncSource.ScheduledSync,
            success: true,
            errorMessage: null,
            userId: departed,
            ct: Arg.Any<CancellationToken>());
    }

    private GoogleGroupSyncService CreateService(params IGoogleGroupMembershipSource[] sources) => new(
        sources,
        _membershipClient,
        _provisioningClient,
        _teamResourceClient,
        _teamResourceService,
        _teamService,
        _userService,
        _identities,
        _userEmailService,
        _syncSettingsService,
        _auditLogService,
        _googleSyncLog,
        _removalNotifications,
        _syncScheduler,
        Options.Create(new GoogleWorkspaceOptions()),
        _clock,
        _logger);

    [HumansFact]
    public async Task ReconcileOneAsync_ClaimOnAMergedAwayId_IsSkippedRatherThanPairedWithTheSurvivor()
    {
        // The resolving read hands a claimed archived id its survivor, but the email rows
        // fetched here are the archived id's own (raw), so the pair is dropped. A survivor a
        // source claims by its live id is still added.
        var archived = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var service = CreateService(new StaticSource("team@nobodies.team", archived));
        StubUsers((archived, "Old", "old@nobodies.team"));
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [archived] = new User { Id = survivor, DisplayName = "Survivor", CreatedAt = _clock.GetCurrentInstant() }
                        .ToUserInfo(),
                }));
        StubGroup("team@nobodies.team", "group-1");

        var diff = await service.ReconcileOneAsync("team@nobodies.team", SyncAction.Execute, Xunit.TestContext.Current.CancellationToken);

        diff.MembersToAdd.Should().BeEmpty();
        await _membershipClient.DidNotReceive()
            .CreateMembershipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void StubUsers(params (Guid UserId, string DisplayName, string Email)[] users)
    {
        foreach (var u in users)
        {
            // BurnerName is the sole resolver source; default it to DisplayName (as CopyNamesToUser
            // does) so callers that only pass a DisplayName still resolve to that name.
            _usersByUserId[u.UserId] = new User
            {
                Id = u.UserId,
                BurnerName = u.DisplayName,
                DisplayName = u.DisplayName,
                CreatedAt = _clock.GetCurrentInstant()
            };
            _usersByUserId[u.UserId].UserEmails.Add(new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = u.UserId,
                Email = u.Email,
                IsVerified = true,
                IsGoogle = true,
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            });
        }

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requested = call.ArgAt<IReadOnlyCollection<Guid>>(0).ToHashSet();
                IReadOnlyDictionary<Guid, UserInfo> dict = _usersByUserId
                    .Where(kv => requested.Contains(kv.Key))
                    .ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ToUserInfo(profile: _profilesByUserId.GetValueOrDefault(kv.Key)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });


        foreach (var u in users)
        {
            _profilesByUserId[u.UserId] = UserFixtures.Profile(
                createdAt: _clock.GetCurrentInstant());
        }
    }

    private void StubProfiles(params (Guid UserId, string? BurnerName)[] profiles)
    {
        foreach (var p in profiles)
        {
            _profilesByUserId[p.UserId] = UserFixtures.Profile(
                burnerName: p.BurnerName ?? string.Empty,
                createdAt: _clock.GetCurrentInstant());

            // Mirror CopyNamesToUser: a Profile save keeps User.BurnerName in sync, and the
            // resolver reads only that field.
            if (_usersByUserId.TryGetValue(p.UserId, out var user))
            {
                user.BurnerName = p.BurnerName ?? string.Empty;
            }
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
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(teamId, "Team", "team", googleGroupPrefix: parts[0]));

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
        _teamService.GetTeamAsync(secondTeamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(secondTeamId, "Duplicate Team", "duplicate-team", googleGroupPrefix: parts[0]));
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
        public List<(string GroupKey, GoogleSyncSource Source)> Enqueued { get; } = [];
        public List<(string GroupKey, TimeSpan Delay, int RetryAttempt, GoogleSyncSource Source)> Scheduled { get; } = [];

        public void Enqueue(string groupKey, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync) => Enqueued.Add((groupKey, syncSource));

        public void Schedule(string groupKey, TimeSpan delay, int retryAttempt, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync) =>
            Scheduled.Add((groupKey, delay, retryAttempt, syncSource));
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

    private static TeamInfo MakeTeamInfo(Guid id, string name, string slug, string? googleGroupPrefix = null) =>
        new(id, name, null, slug, IsActive: true, IsSystemTeam: false,
            SystemTeamType.None, RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, SystemClock.Instance.GetCurrentInstant(), [],
            GoogleGroupPrefix: googleGroupPrefix);

}
