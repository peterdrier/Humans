using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.HumanLifecycle;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Services.HumanLifecycle;

/// <summary>
/// Unit tests for the lifecycle state-machine surface extracted from
/// <c>OnboardingService</c> in nobodies-collective#583. Verifies that
/// suspend/unsuspend produce the same writes, notifications, and metric
/// emissions as the original onboarding-bundled implementation.
/// </summary>
public class HumanLifecycleServiceTests
{
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly INotificationService _notificationService = Substitute.For<INotificationService>();
    private readonly INotificationInboxService _notificationInboxService = Substitute.For<INotificationInboxService>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    private HumanLifecycleService BuildSut() =>
        new(
            _profileService,
            _notificationService,
            _notificationInboxService,
            _metrics,
            NullLogger<HumanLifecycleService>.Instance);

    [HumansFact]
    public async Task SuspendAsync_OnSuccess_WritesProfileNotifiesAndRecordsMetric()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        const string notes = "Disruptive behaviour";

        _profileService.SetSuspendedAsync(userId, adminId, suspended: true, notes, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var sut = BuildSut();

        var result = await sut.SuspendAsync(userId, adminId, notes);

        result.Success.Should().BeTrue();
        await _profileService.Received(1)
            .SetSuspendedAsync(userId, adminId, suspended: true, notes, Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendAsync(
            NotificationSource.AccessSuspended,
            NotificationClass.Actionable,
            NotificationPriority.Critical,
            "Your access has been suspended",
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids.Contains(userId)),
            Arg.Is<string?>(b => b != null && b.Contains(notes)),
            "/Profile",
            "View profile",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordMemberSuspended("admin");
    }

    [HumansFact]
    public async Task SuspendAsync_WithoutNotes_UsesGenericNotificationBody()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        _profileService.SetSuspendedAsync(userId, adminId, true, null, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var sut = BuildSut();

        await sut.SuspendAsync(userId, adminId, notes: null);

        await _notificationService.Received(1).SendAsync(
            NotificationSource.AccessSuspended,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            "Your access has been suspended by an administrator.",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SuspendAsync_WhenProfileWriteFails_ShortCircuitsAndDoesNotNotifyOrMeter()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        _profileService.SetSuspendedAsync(userId, adminId, true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "NotFound"));

        var sut = BuildSut();

        var result = await sut.SuspendAsync(userId, adminId, notes: null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        await _notificationService.DidNotReceiveWithAnyArgs().SendAsync(
            default, default, default, null!, null!);
        _metrics.DidNotReceive().RecordMemberSuspended(Arg.Any<string>());
    }

    [HumansFact]
    public async Task UnsuspendAsync_OnSuccess_WritesProfileAndResolvesSuspendedNotifications()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        _profileService.SetSuspendedAsync(userId, adminId, suspended: false, null, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var sut = BuildSut();

        var result = await sut.UnsuspendAsync(userId, adminId);

        result.Success.Should().BeTrue();
        await _profileService.Received(1)
            .SetSuspendedAsync(userId, adminId, suspended: false, null, Arg.Any<CancellationToken>());
        await _notificationInboxService.Received(1)
            .ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UnsuspendAsync_WhenProfileWriteFails_ShortCircuitsAndDoesNotResolveNotifications()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        _profileService.SetSuspendedAsync(userId, adminId, false, null, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "NotFound"));

        var sut = BuildSut();

        var result = await sut.UnsuspendAsync(userId, adminId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        await _notificationInboxService.DidNotReceiveWithAnyArgs()
            .ResolveBySourceAsync(Guid.Empty, default, CancellationToken.None);
    }

    [HumansFact]
    public async Task SuspendAsync_WhenNotificationDispatchThrows_StillReturnsSuccessAndRecordsMetric()
    {
        // Notification dispatch failures must not surface to the caller — the
        // profile write already succeeded; suspension is durable. Mirrors the
        // try/catch contract in the original OnboardingService implementation.
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        _profileService.SetSuspendedAsync(userId, adminId, true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
        _notificationService.SendAsync(
                Arg.Any<NotificationSource>(),
                Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("notification down"));

        var sut = BuildSut();

        var result = await sut.SuspendAsync(userId, adminId, notes: null);

        result.Success.Should().BeTrue();
        _metrics.Received(1).RecordMemberSuspended("admin");
    }

    [HumansFact]
    public async Task UnsuspendAsync_WhenInboxResolutionThrows_StillReturnsSuccess()
    {
        // Inbox-resolution failures must not surface to the caller — the
        // profile write already succeeded and unsuspension is durable.
        // Mirrors the try/catch contract on the SuspendAsync notification path.
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        _profileService.SetSuspendedAsync(userId, adminId, suspended: false, null, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
        _notificationInboxService
            .ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("inbox down"));

        var sut = BuildSut();

        var result = await sut.UnsuspendAsync(userId, adminId);

        result.Success.Should().BeTrue();
    }
}
