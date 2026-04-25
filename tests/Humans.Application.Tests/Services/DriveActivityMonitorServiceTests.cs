using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using DriveActivityMonitorService = Humans.Application.Services.GoogleIntegration.DriveActivityMonitorService;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Behavioral tests for the §15-migrated
/// <see cref="DriveActivityMonitorService"/>. The service is a dispatcher
/// over three collaborators — <see cref="IGoogleDriveActivityClient"/>,
/// <see cref="ITeamResourceService"/>, and
/// <see cref="IDriveActivityMonitorRepository"/> — so tests substitute all
/// three and pin down: self-initiated changes get filtered, anomaly descriptions
/// are built correctly, partial-failure keeps the last-run marker, and the
/// happy path advances the marker.
/// </summary>
public class DriveActivityMonitorServiceTests
{
    private readonly IGoogleDriveActivityClient _client;
    private readonly ITeamResourceService _teamResources;
    private readonly IDriveActivityMonitorRepository _repository;
    private readonly FakeClock _clock;
    private readonly DriveActivityMonitorService _service;

    private const string ServiceAccountEmail = "humans-sa@example.iam.gserviceaccount.com";
    private const string ServiceAccountClientId = "1234567890";

    public DriveActivityMonitorServiceTests()
    {
        _client = Substitute.For<IGoogleDriveActivityClient>();
        _teamResources = Substitute.For<ITeamResourceService>();
        _repository = Substitute.For<IDriveActivityMonitorRepository>();
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 22, 10, 0));

        _client.IsConfigured.Returns(true);
        _client.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>()).Returns(ServiceAccountEmail);
        _client.GetServiceAccountClientIdAsync(Arg.Any<CancellationToken>()).Returns(ServiceAccountClientId);

        _service = new DriveActivityMonitorService(
            _client, _teamResources, _repository, _clock,
            NullLogger<DriveActivityMonitorService>.Instance);
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_WithNoResources_ReturnsZeroAndDoesNotHitApi()
    {
        _teamResources.GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GoogleResource>());

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(0);
        _client.DidNotReceiveWithAnyArgs().QueryActivityAsync(default!, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().PersistAnomaliesAsync(default!, default, default);
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
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a => a.Count == 0),
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
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
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a =>
                a.Count == 1 &&
                a[0].Action == AuditAction.AnomalousPermissionDetected &&
                a[0].EntityType == nameof(GoogleResource) &&
                a[0].EntityId == resource.Id &&
                a[0].Description.Contains("Sensitive-Drive") &&
                a[0].Description.Contains("intruder@example.com") &&
                a[0].Description.Contains("added writer")),
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
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
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a =>
                a.Count == 1 &&
                a[0].Description.Contains("resolved@example.com")),
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());

        // Directory API should be hit exactly once per unique people/ id (per-run cache).
        await _client.Received(1).TryResolvePersonEmailAsync("people/9999", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CheckForAnomalousActivityAsync_FallsBackToLocalDbWhenDirectoryLookupFails()
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
        _repository.TryResolveEmailByGoogleUserIdAsync("42", Arg.Any<CancellationToken>())
            .Returns("localdb@example.com");

        var count = await _service.CheckForAnomalousActivityAsync();

        count.Should().Be(1);
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a =>
                a.Count == 1 && a[0].Description.Contains("localdb@example.com")),
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
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
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a => a.Count == 1),
            (Instant?)null,
            Arg.Any<CancellationToken>());
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
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a => a.Count == 0),
            _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
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
        await _repository.Received(1).PersistAnomaliesAsync(
            Arg.Is<IReadOnlyList<AuditLogEntry>>(a => a.Count == 0),
            (Instant?)null,
            Arg.Any<CancellationToken>());
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

    private void SeedResources(params GoogleResource[] resources)
    {
        _teamResources.GetActiveDriveFoldersAsync(Arg.Any<CancellationToken>()).Returns(resources);
    }

    private void SeedActivity(string googleId, params DriveActivityEvent[] events)
    {
        _client.QueryActivityAsync(googleId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(events));
    }

    private static GoogleResource BuildResource(string name) => new()
    {
        Id = Guid.NewGuid(),
        GoogleId = $"gid-{Guid.NewGuid():N}",
        Name = name,
        ResourceType = GoogleResourceType.DriveFolder,
        TeamId = Guid.NewGuid(),
    };

    private static DriveActivityEvent BuildPermissionChangeEvent(
        string actorPersonName, string addedRole, string targetUser) =>
        new(
            Actors: new[]
            {
                new DriveActivityActor(
                    KnownUserPersonName: actorPersonName,
                    IsAdministrator: false,
                    IsSystem: false),
            },
            PermissionChange: new DriveActivityPermissionChange(
                AddedPermissions: new[]
                {
                    new DriveActivityPermission(
                        Role: addedRole,
                        UserPersonName: targetUser,
                        GroupEmail: null,
                        DomainName: null,
                        IsAnyone: false),
                },
                RemovedPermissions: Array.Empty<DriveActivityPermission>()));

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
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<DriveActivityEvent> ThrowNotFoundEnumerable(string googleItemId)
    {
        await Task.Yield();
        throw new DriveActivityResourceNotFoundException(googleItemId);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
