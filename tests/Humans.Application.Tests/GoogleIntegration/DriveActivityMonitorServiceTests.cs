using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using DriveActivityMonitorService = Humans.Application.Services.GoogleIntegration.DriveActivityMonitorService;

namespace Humans.Application.Tests.GoogleIntegration;

/// <summary>
/// Behavioral tests for the §15-migrated
/// <see cref="DriveActivityMonitorService"/>. The service is a dispatcher
/// over four collaborators — <see cref="IGoogleDriveActivityClient"/>,
/// <see cref="ITeamResourceService"/>,
/// <see cref="IDriveActivityMonitorRepository"/>, and
/// <see cref="IAuditLogService"/> — so tests substitute all four and pin down:
/// self-initiated changes get filtered, anomaly descriptions are built
/// correctly and emitted through <see cref="IAuditLogService"/>,
/// partial-failure keeps the last-run marker, and the happy path advances it.
/// </summary>
public class DriveActivityMonitorServiceTests
{
    private readonly IGoogleDriveActivityClient _client;
    private readonly ITeamResourceService _teamResources;
    private readonly IDriveActivityMonitorRepository _repository;
    private readonly IUserServiceRead _userService;
    private readonly IAuditLogService _auditLog;
    private readonly FakeClock _clock;
    private readonly DriveActivityMonitorService _service;

    private const string ServiceAccountEmail = "humans-sa@example.iam.gserviceaccount.com";
    private const string ServiceAccountClientId = "1234567890";
    private const string JobName = "DriveActivityMonitorJob";

    public DriveActivityMonitorServiceTests()
    {
        _client = Substitute.For<IGoogleDriveActivityClient>();
        _teamResources = Substitute.For<ITeamResourceService>();
        _repository = Substitute.For<IDriveActivityMonitorRepository>();
        _userService = Substitute.For<IUserServiceRead>();
        _auditLog = Substitute.For<IAuditLogService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 22, 10, 0));

        _client.IsConfigured.Returns(true);
        _client.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>()).Returns(ServiceAccountEmail);
        _client.GetServiceAccountClientIdAsync(Arg.Any<CancellationToken>()).Returns(ServiceAccountClientId);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns([]);

        _service = new DriveActivityMonitorService(
            _client, _teamResources, _repository, _userService, _auditLog, _clock,
            NullLogger<DriveActivityMonitorService>.Instance);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_WithNoResources_ReturnsZeroAndDoesNotHitApi()
    {
        _teamResources.GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(0);
        _client.DidNotReceiveWithAnyArgs().QueryActivityAsync(null!, null!, CancellationToken.None);
        await _repository.DidNotReceiveWithAnyArgs().AdvanceLastRunMarkerAsync(null, CancellationToken.None);
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, default, null!, default(string)!);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_FiltersSelfInitiatedChangesByEmailAndClientId()
    {
        var resource = BuildResource("Drive-One");
        SeedResources(resource);

        var emailEvent = BuildPermissionChangeEvent(
            actorPersonName: ServiceAccountEmail,
            addedRole: "writer",
            targetUser: "someone@example.com");
        var clientIdEvent = BuildPermissionChangeEvent(
            actorPersonName: $"people/{ServiceAccountClientId}",
            addedRole: "reader",
            targetUser: "another@example.com");

        SeedActivity(resource.GoogleId, emailEvent, clientIdEvent);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(0);
        // Marker still advances because no failures occurred.
        await _repository.Received(1).AdvanceLastRunMarkerAsync(
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
        // No anomalies → no audit entries emitted.
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, default, null!, default(string)!);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_RecordsAnomalyForThirdPartyChange()
    {
        var resource = BuildResource("Sensitive-Drive");
        SeedResources(resource);

        var anomalousEvent = BuildPermissionChangeEvent(
            actorPersonName: "intruder@example.com",
            addedRole: "writer",
            targetUser: "intruder@example.com");
        SeedActivity(resource.GoogleId, anomalousEvent);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _repository.Received(1).AdvanceLastRunMarkerAsync(
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            resource.Id,
            Arg.Is<string>(d =>
                d.Contains("Sensitive-Drive", StringComparison.Ordinal) &&
                d.Contains("intruder@example.com", StringComparison.Ordinal) &&
                d.Contains("added writer", StringComparison.Ordinal)),
            JobName);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_ResolvesPeopleIdViaDirectoryConnector()
    {
        var resource = BuildResource("Resolved-Drive");
        SeedResources(resource);

        var anomalousEvent = BuildPermissionChangeEvent(
            actorPersonName: "people/9999",
            addedRole: "owner",
            targetUser: "people/9999");
        SeedActivity(resource.GoogleId, anomalousEvent);

        _client.TryResolvePersonEmailAsync("people/9999", Arg.Any<CancellationToken>())
            .Returns("resolved@example.com");

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            resource.Id,
            Arg.Is<string>(d => d.Contains("resolved@example.com", StringComparison.Ordinal)),
            JobName);

        // Directory API should be hit exactly once per unique people/ id (per-run cache).
        await _client.Received(1).TryResolvePersonEmailAsync("people/9999", Arg.Any<CancellationToken>());
        await _userService.DidNotReceive().GetAllUserInfosAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_DoesNotLoadUserInfo_WhenAllPeopleIdsResolvedByDirectory()
    {
        var resource = BuildResource("MultiActor-Drive");
        SeedResources(resource);

        var eventA = BuildPermissionChangeEvent(
            actorPersonName: "people/111",
            addedRole: "writer",
            targetUser: "people/111");
        var eventB = BuildPermissionChangeEvent(
            actorPersonName: "people/222",
            addedRole: "reader",
            targetUser: "people/222");
        SeedActivity(resource.GoogleId, eventA, eventB);

        _client.TryResolvePersonEmailAsync("people/111", Arg.Any<CancellationToken>())
            .Returns("actor-a@example.com");
        _client.TryResolvePersonEmailAsync("people/222", Arg.Any<CancellationToken>())
            .Returns("actor-b@example.com");

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(2);
        await _userService.DidNotReceive().GetAllUserInfosAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_LeavesRawPeopleIdWhenUserInfoHasNullEmail()
    {
        var resource = BuildResource("NullEmail-Drive");
        SeedResources(resource);

        var anomalousEvent = BuildPermissionChangeEvent(
            actorPersonName: "people/99",
            addedRole: "writer",
            targetUser: "people/99");
        SeedActivity(resource.GoogleId, anomalousEvent);

        _client.TryResolvePersonEmailAsync("people/99", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns([
                BuildUserInfoWithNullEmail(new UserExternalLoginInfo("Google", "99"))
            ]);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            resource.Id,
            Arg.Is<string>(d =>
                d.Contains("people/99", StringComparison.Ordinal) &&
                !d.Contains("Skipping ambiguous", StringComparison.Ordinal)),
            JobName);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_FallsBackToUserInfoWhenDirectoryLookupFails()
    {
        var resource = BuildResource("Fallback-Drive");
        SeedResources(resource);

        var anomalousEvent = BuildPermissionChangeEvent(
            actorPersonName: "people/42",
            addedRole: "reader",
            targetUser: "people/42");
        SeedActivity(resource.GoogleId, anomalousEvent);

        _client.TryResolvePersonEmailAsync("people/42", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns([
                BuildUserInfo(
                    "userinfo@example.com",
                    new UserExternalLoginInfo("Google", "42"))
            ]);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            resource.Id,
            Arg.Is<string>(d => d.Contains("userinfo@example.com", StringComparison.Ordinal)),
            JobName);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_LeavesRawPeopleIdWhenUserInfoFallbackFails()
    {
        var resource = BuildResource("Fallback-Failure-Drive");
        SeedResources(resource);

        var anomalousEvent = BuildPermissionChangeEvent(
            actorPersonName: "people/42",
            addedRole: "reader",
            targetUser: "people/42");
        SeedActivity(resource.GoogleId, anomalousEvent);

        _client.TryResolvePersonEmailAsync("people/42", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyCollection<UserInfo>>(
                new InvalidOperationException("user cache failed")));

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _repository.Received(1).AdvanceLastRunMarkerAsync(
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            resource.Id,
            Arg.Is<string>(d => d.Contains("people/42", StringComparison.Ordinal)),
            JobName);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_SkipsAmbiguousUserInfoProviderKeys()
    {
        var resource = BuildResource("Ambiguous-Drive");
        SeedResources(resource);

        var anomalousEvent = BuildPermissionChangeEvent(
            actorPersonName: "people/42",
            addedRole: "reader",
            targetUser: "people/42");
        SeedActivity(resource.GoogleId, anomalousEvent);

        _client.TryResolvePersonEmailAsync("people/42", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns([
                BuildUserInfo(
                    "first@example.com",
                    new UserExternalLoginInfo("Google", "42")),
                BuildUserInfo(
                    "second@example.com",
                    new UserExternalLoginInfo("Google", "42"))
            ]);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            resource.Id,
            Arg.Is<string>(d =>
                d.Contains("people/42", StringComparison.Ordinal) &&
                !d.Contains("first@example.com", StringComparison.Ordinal) &&
                !d.Contains("second@example.com", StringComparison.Ordinal)),
            JobName);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_WithPartialFailure_DoesNotAdvanceMarker()
    {
        var ok = BuildResource("Ok-Drive");
        var broken = BuildResource("Broken-Drive");
        SeedResources(ok, broken);

        var okEvent = BuildPermissionChangeEvent(
            actorPersonName: "intruder@example.com",
            addedRole: "reader",
            targetUser: "intruder@example.com");
        SeedActivity(ok.GoogleId, okEvent);

        // Configure an async-enumerable that throws on MoveNext for the broken resource.
        _client.QueryActivityAsync(broken.GoogleId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingEnumerable());

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        // Partial failure → marker not advanced, but the anomaly is still audited.
        await _repository.Received(1).AdvanceLastRunMarkerAsync(
            null,
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            ok.Id,
            Arg.Any<string>(),
            JobName);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_DownGradesResourceNotFoundToWarning()
    {
        var missing = BuildResource("Missing-Drive");
        SeedResources(missing);

        _client.QueryActivityAsync(missing.GoogleId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowNotFoundEnumerable(missing.GoogleId));

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(0);
        // 404 is expected and does NOT count as a failure — marker advances.
        await _repository.Received(1).AdvanceLastRunMarkerAsync(
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, default, null!, default(string)!);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_WithUnconfiguredClient_DoesNotAdvanceMarker()
    {
        // Simulates running the monitor in dev against StubGoogleDriveActivityClient:
        // the stub returns no events, so without this guard the marker would advance
        // to "now" and silently skip every historical permission change once the same
        // database later gains real Google credentials.
        _client.IsConfigured.Returns(false);

        var resource = BuildResource("Unconfigured-Drive");
        SeedResources(resource);
        SeedActivity(resource.GoogleId /* no events */);

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(0);
        await _repository.Received(1).AdvanceLastRunMarkerAsync(
            null,
            Arg.Any<CancellationToken>());
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, default, null!, default(string)!);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_UsesLookbackDefault_OnFirstRun()
    {
        var resource = BuildResource("First-Drive");
        SeedResources(resource);
        _repository.GetLastRunTimestampAsync(Arg.Any<CancellationToken>()).Returns((Instant?)null);

        SeedActivity(resource.GoogleId /* no events */);

        await _service.CheckForAnomalousActivityAsync();

        // The filter should be 24h before "now" on first run.
        var expectedLookback = _clock.GetCurrentInstant().Minus(Duration.FromHours(24));
        var expectedFilter = NodaTime.Text.InstantPattern.General.Format(expectedLookback);
        _client.Received(1).QueryActivityAsync(resource.GoogleId, expectedFilter, Arg.Any<CancellationToken>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SeedResources(params GoogleResourceSnapshot[] resources)
    {
        _teamResources.GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>()).Returns(resources);
    }

    private void SeedActivity(string googleId, params DriveActivityEvent[] events)
    {
        _client.QueryActivityAsync(googleId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(events));
    }

    private static GoogleResourceSnapshot BuildResource(string name) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        $"gid-{Guid.NewGuid():N}",
        name,
        GoogleResourceType.DriveFolder,
        Url: null);

    private UserInfo BuildUserInfo(string email, params UserExternalLoginInfo[] externalLogins) =>
        new(
            Guid.NewGuid(),
            BurnerName: email,
            IsGdprAnonymized: false,
            PreferredLanguage: "en",
            FallbackPictureUrl: null,
            CreatedAt: _clock.GetCurrentInstant(),
            LastLoginAt: null,
            LastConsentReminderSentAt: null,
            DeletionRequestedAt: null,
            DeletionScheduledFor: null,
            DeletionEligibleAfter: null,
            UnsubscribedFromCampaigns: false,
            ICalToken: null,
            SuppressScheduleChangeEmails: false,
            MagicLinkSentAt: null,
            GoogleEmailStatus: GoogleEmailStatus.Unknown,
            ContactSource: null,
            ExternalSourceId: null,
            MergedToUserId: null,
            MergedAt: null,
            IdentityEmailColumn: email,
            UserEmails: [],
            EventParticipations: [],
            ExternalLogins: externalLogins,
            Profile: null,
            CommunicationPreferences: []);

    private UserInfo BuildUserInfoWithNullEmail(params UserExternalLoginInfo[] externalLogins) =>
        new(
            Guid.NewGuid(),
            BurnerName: "Deleted User",
            IsGdprAnonymized: true,
            PreferredLanguage: "en",
            FallbackPictureUrl: null,
            CreatedAt: _clock.GetCurrentInstant(),
            LastLoginAt: null,
            LastConsentReminderSentAt: null,
            DeletionRequestedAt: null,
            DeletionScheduledFor: null,
            DeletionEligibleAfter: null,
            UnsubscribedFromCampaigns: false,
            ICalToken: null,
            SuppressScheduleChangeEmails: false,
            MagicLinkSentAt: null,
            GoogleEmailStatus: GoogleEmailStatus.Unknown,
            ContactSource: null,
            ExternalSourceId: null,
            MergedToUserId: null,
            MergedAt: null,
            IdentityEmailColumn: null,
            UserEmails: [],
            EventParticipations: [],
            ExternalLogins: externalLogins,
            Profile: null,
            CommunicationPreferences: []);

    private static DriveActivityEvent BuildPermissionChangeEvent(
        string actorPersonName, string addedRole, string targetUser) =>
        new(
            Actors:
            [
                new DriveActivityActor(
                    KnownUserPersonName: actorPersonName,
                    IsAdministrator: false,
                    IsSystem: false)
            ],
            PermissionChange: new DriveActivityPermissionChange(
                AddedPermissions:
                [
                    new DriveActivityPermission(
                        Role: addedRole,
                        UserPersonName: targetUser,
                        GroupEmail: null,
                        DomainName: null,
                        IsAnyone: false)
                ],
                RemovedPermissions: []));

    private static async IAsyncEnumerable<DriveActivityEvent> ToAsyncEnumerable(
        IEnumerable<DriveActivityEvent> source)
    {
        foreach (var e in source)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static async IAsyncEnumerable<DriveActivityEvent> ThrowingEnumerable()
    {
        await Task.Yield();
        throw new InvalidOperationException("boom");
#pragma warning disable CS0162 // Unreachable code — required for iterator signature.
        // ReSharper disable once HeuristicUnreachableCode
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<DriveActivityEvent> ThrowNotFoundEnumerable(string googleItemId)
    {
        await Task.Yield();
        throw new DriveActivityResourceNotFoundException(googleItemId);
#pragma warning disable CS0162
        // ReSharper disable once HeuristicUnreachableCode
        yield break;
#pragma warning restore CS0162
    }
}
