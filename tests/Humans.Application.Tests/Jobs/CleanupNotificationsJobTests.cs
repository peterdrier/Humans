using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Jobs;

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

        var metrics = new HumansMetricsService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<HumansMetricsService>>());
        _job = new CleanupNotificationsJob(_dbContext, _clock, metrics, NullLogger<CleanupNotificationsJob>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
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

        // Unresolved — should NOT be deleted
        var unresolved = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Unresolved",
            Source = NotificationSource.ShiftCoverageGap,
            Class = NotificationClass.Actionable,
            Priority = NotificationPriority.High,
            CreatedAt = now - Duration.FromDays(20),
        };

        await _dbContext.Notifications.AddRangeAsync(oldResolved, recentResolved, unresolved);
        await _dbContext.SaveChangesAsync();

        await _job.ExecuteAsync();

        var remaining = await _dbContext.Notifications.ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Select(n => n.Title).Should().Contain("Recent resolved");
        remaining.Select(n => n.Title).Should().Contain("Unresolved");
    }
}
