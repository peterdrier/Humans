using NSubstitute;
using Microsoft.Extensions.Logging;
using Humans.Base.Interfaces;
using Humans.Notifications.Jobs;
using Humans.Notifications.Data;
using Humans.Notifications.Services;
using AwesomeAssertions;
using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Notifications.Tests.Services;

/// <summary>
/// The retention rule <c>CleanupNotificationsJob</c> drives through
/// <see cref="INotificationRetention"/>. The job itself is a scheduler shim over this
/// one call, so the cutoffs are asserted here rather than through Hangfire.
/// </summary>
public class NotificationRetentionTests : IDisposable
{
    private readonly NotificationsDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly INotificationRetention _retention;

    public NotificationRetentionTests()
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new NotificationsDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 10, 12, 0));

        var repo = new NotificationRepository(new TestDbContextFactory<NotificationsDbContext>(options));
        _retention = new NotificationInboxService(
            repo, TestSources.Registry, _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansTheory]
    [Xunit.InlineData("Shutdown")]
    [Xunit.InlineData("DependencyCancellation")]
    [Xunit.InlineData("Fault")]
    public async Task RetentionJob_DistinguishesShutdownFromFaults(string kind)
    {
        using var shutdown = new CancellationTokenSource();
        var failure = string.Equals(kind, "Fault", StringComparison.Ordinal)
            ? (Exception)new IOException("Retention unavailable")
            : new OperationCanceledException(string.Equals(kind, "Shutdown", StringComparison.Ordinal)
                ? shutdown.Token : new CancellationToken(true));
        var retention = Substitute.For<INotificationRetention>();
        retention.PurgeExpiredAsync(shutdown.Token).Returns(async Task<(int, int, int)> (_) =>
        {
            if (string.Equals(kind, "Shutdown", StringComparison.Ordinal)) await shutdown.CancelAsync();
            throw failure;
        });
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<CleanupNotificationsJob>();
        var job = new CleanupNotificationsJob(retention, metrics, logger);

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        await retention.Received(1).PurgeExpiredAsync(shutdown.Token);
        metrics.DidNotReceive().RecordJobRun("cleanup_notifications", "success");
        if (string.Equals(kind, "Shutdown", StringComparison.Ordinal))
        {
            metrics.DidNotReceive().RecordJobRun("cleanup_notifications", "failure");
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
            logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        }
        else
        {
            metrics.Received(1).RecordJobRun("cleanup_notifications", "failure");
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception == failure);
        }
    }

    [HumansFact]
    public async Task RetentionJob_RecordsSuccessAfterThePurgeCompletes()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var retention = Substitute.For<INotificationRetention>();
        retention.PurgeExpiredAsync(ct).Returns((2, 3, 4));
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<CleanupNotificationsJob>();
        var job = new CleanupNotificationsJob(retention, metrics, logger);

        await job.ExecuteAsync(ct);

        await retention.Received(1).PurgeExpiredAsync(ct);
        metrics.Received(1).RecordJobRun("cleanup_notifications", "success");
        metrics.DidNotReceive().RecordJobRun("cleanup_notifications", "failure");
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
    }

    [HumansFact]
    public async Task DeletesResolvedNotificationsOlderThan7Days()
    {
        var now = _clock.GetCurrentInstant();
        var userId = Guid.NewGuid();

        // Old resolved (8 days ago) — should be deleted
        var oldResolved = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Old resolved",
            Source = TestSources.TeamUpdate.Name,
            Class = NotificationClass.Informational,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromDays(10),
            ResolvedAt = now - Duration.FromDays(8),
            ResolvedByUserId = userId,
        };

        // Recently resolved (2 days ago) — should NOT be deleted
        var recentResolved = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Recent resolved",
            Source = TestSources.TeamUpdate.Name,
            Class = NotificationClass.Informational,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromDays(5),
            ResolvedAt = now - Duration.FromDays(2),
            ResolvedByUserId = userId,
        };

        // Unresolved actionable — should NOT be deleted (actionable are never auto-cleaned)
        var unresolvedActionable = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Unresolved actionable",
            Source = TestSources.ShiftUpdate.Name,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.High,
            CreatedAt = now - Duration.FromDays(20),
        };

        await _dbContext.Notifications.AddRangeAsync(oldResolved, recentResolved, unresolvedActionable);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.Notifications.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().HaveCount(2);
        remaining.Select(n => n.Title).Should().Contain("Recent resolved");
        remaining.Select(n => n.Title).Should().Contain("Unresolved actionable");
    }

    [HumansFact(Timeout = 10000)]
    public async Task DeletesStaleInformationalNotificationsOlderThan30Days()
    {
        var now = _clock.GetCurrentInstant();

        // Unresolved informational, 35 days old — should be deleted
        var staleInformational = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Stale informational",
            Source = TestSources.TeamUpdate.Name,
            Class = NotificationClass.Informational,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromDays(35),
        };

        // Unresolved informational, 10 days old — should NOT be deleted
        var recentInformational = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Recent informational",
            Source = TestSources.TeamUpdate.Name,
            Class = NotificationClass.Informational,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromDays(10),
        };

        // Unresolved actionable of a LIVE source, 60 days old — should NOT be deleted
        // (actionable of live sources are never auto-cleaned).
        var oldActionable = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Old actionable",
            Source = TestSources.ShiftUpdate.Name,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.High,
            CreatedAt = now - Duration.FromDays(60),
        };

        await _dbContext.Notifications.AddRangeAsync(staleInformational, recentInformational, oldActionable);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.Notifications.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().HaveCount(2);
        remaining.Select(n => n.Title).Should().Contain("Recent informational");
        remaining.Select(n => n.Title).Should().Contain("Old actionable");
    }

    [HumansFact]
    public async Task DeletesUnresolvedRetiredSourceNotifications()
    {
        var now = _clock.GetCurrentInstant();

        // Retired-source actionable (no longer emitted, no resolution path) — purged
        // regardless of age, even though it is actionable.
        var retired = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Legacy application submitted",
            Source = TestSources.Retired.Name,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromHours(1),
        };

        // Live-source actionable — survives.
        var live = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Live issue",
            Source = TestSources.Plain.Name,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromHours(1),
        };

        await _dbContext.Notifications.AddRangeAsync(retired, live);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.Notifications.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle().Which.Title.Should().Be("Live issue");
    }
}
