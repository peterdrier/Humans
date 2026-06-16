using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Notifications;

public class CleanupNotificationsJobTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CleanupNotificationsJob _job;

    public CleanupNotificationsJobTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 10, 12, 0));

        var metrics = TestMetrics.Create();
        var repo = new NotificationRepository(new TestDbContextFactory(options));
        _job = new CleanupNotificationsJob(repo, _clock, metrics, NullLogger<CleanupNotificationsJob>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
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
            Source = NotificationSource.TeamMemberAdded,
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
            Source = NotificationSource.TeamMemberAdded,
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
            Source = NotificationSource.ShiftCoverageGap,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.High,
            CreatedAt = now - Duration.FromDays(20),
        };

        await _dbContext.Notifications.AddRangeAsync(oldResolved, recentResolved, unresolvedActionable);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

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
            Source = NotificationSource.TeamMemberAdded,
            Class = NotificationClass.Informational,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromDays(35),
        };

        // Unresolved informational, 10 days old — should NOT be deleted
        var recentInformational = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Recent informational",
            Source = NotificationSource.TeamMemberAdded,
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
            Source = NotificationSource.ShiftCoverageGap,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.High,
            CreatedAt = now - Duration.FromDays(60),
        };

        await _dbContext.Notifications.AddRangeAsync(staleInformational, recentInformational, oldActionable);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

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
            Source = NotificationSource.ApplicationSubmitted,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromHours(1),
        };

        // Live-source actionable — survives.
        var live = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Live issue",
            Source = NotificationSource.IssueSubmitted,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.Normal,
            CreatedAt = now - Duration.FromHours(1),
        };

        await _dbContext.Notifications.AddRangeAsync(retired, live);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.Notifications.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle().Which.Title.Should().Be("Live issue");
    }
}
