using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Notifications;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public class NotificationRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly NotificationRepository _repo;
    private readonly Instant _now = Instant.FromUtc(2026, 4, 10, 12, 0);

    public NotificationRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _repo = new NotificationRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddAsync_PersistsNotificationWithRecipients()
    {
        var userId = Guid.NewGuid();
        var notification = CreateNotification(userId, NotificationClass.Informational);

        await _repo.AddAsync(notification);

        var stored = await _dbContext.Notifications
            .AsNoTracking()
            .Include(n => n.Recipients)
            .SingleAsync();
        stored.Title.Should().Be("Test");
        stored.Recipients.Should().ContainSingle(r => r.UserId == userId);
    }

    [Fact]
    public async Task AddRangeAsync_PersistsAllNotifications()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        await _repo.AddRangeAsync([
            CreateNotification(u1),
            CreateNotification(u2)
        ]);

        (await _dbContext.Notifications.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNotFound_ForMissingNotification()
    {
        var outcome = await _repo.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), _now);

        outcome.Success.Should().BeFalse();
        outcome.NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ReturnsForbidden_WhenActorNotRecipient()
    {
        var recipient = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var n = CreateNotification(recipient, NotificationClass.Actionable);
        await _repo.AddAsync(n);

        var outcome = await _repo.ResolveAsync(n.Id, actor, _now);

        outcome.Success.Should().BeFalse();
        outcome.Forbidden.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_SetsResolvedFields()
    {
        var userId = Guid.NewGuid();
        var n = CreateNotification(userId, NotificationClass.Actionable);
        await _repo.AddAsync(n);

        var outcome = await _repo.ResolveAsync(n.Id, userId, _now);

        outcome.Success.Should().BeTrue();
        outcome.AffectedUserIds.Should().Contain(userId);

        var stored = await _dbContext.Notifications.AsNoTracking().SingleAsync();
        stored.ResolvedAt.Should().Be(_now);
        stored.ResolvedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task DismissAsync_ForbidsActionable()
    {
        var userId = Guid.NewGuid();
        var n = CreateNotification(userId, NotificationClass.Actionable);
        await _repo.AddAsync(n);

        var outcome = await _repo.DismissAsync(n.Id, userId, _now);

        outcome.Success.Should().BeFalse();
        outcome.Forbidden.Should().BeTrue();
    }

    [Fact]
    public async Task DismissAsync_PermitsInformational()
    {
        var userId = Guid.NewGuid();
        var n = CreateNotification(userId, NotificationClass.Informational);
        await _repo.AddAsync(n);

        var outcome = await _repo.DismissAsync(n.Id, userId, _now);

        outcome.Success.Should().BeTrue();
        var stored = await _dbContext.Notifications.AsNoTracking().SingleAsync();
        stored.ResolvedAt.Should().Be(_now);
    }

    [Fact]
    public async Task MarkAllReadAsync_UpdatesAllUnreadRecipientRows()
    {
        var userId = Guid.NewGuid();
        await _repo.AddAsync(CreateNotification(userId));
        await _repo.AddAsync(CreateNotification(userId));

        var updated = await _repo.MarkAllReadAsync(userId, _now);

        updated.Should().Be(2);
        var anyUnread = await _dbContext.NotificationRecipients
            .AsNoTracking()
            .AnyAsync(nr => nr.UserId == userId && nr.ReadAt == null);
        anyUnread.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteResolvedOlderThanAsync_DeletesOnlyOlderResolved()
    {
        var userId = Guid.NewGuid();

        var old = CreateNotification(userId, NotificationClass.Informational);
        old.ResolvedAt = _now - Duration.FromDays(8);
        old.ResolvedByUserId = userId;

        var recent = CreateNotification(userId, NotificationClass.Informational);
        recent.ResolvedAt = _now - Duration.FromHours(1);
        recent.ResolvedByUserId = userId;

        var unresolved = CreateNotification(userId, NotificationClass.Informational);

        await _repo.AddRangeAsync([old, recent, unresolved]);

        var deleted = await _repo.DeleteResolvedOlderThanAsync(_now - Duration.FromDays(7));

        deleted.Should().Be(1);
        (await _dbContext.Notifications.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeleteUnresolvedInformationalOlderThanAsync_IgnoresActionable()
    {
        var userId = Guid.NewGuid();
        var cutoff = _now - Duration.FromDays(30);

        var staleInfo = CreateNotification(userId, NotificationClass.Informational, createdAt: _now - Duration.FromDays(40));
        var staleActionable = CreateNotification(userId, NotificationClass.Actionable, createdAt: _now - Duration.FromDays(40));

        await _repo.AddRangeAsync([staleInfo, staleActionable]);

        var deleted = await _repo.DeleteUnresolvedInformationalOlderThanAsync(cutoff);

        deleted.Should().Be(1);
        var remaining = await _dbContext.Notifications.AsNoTracking().SingleAsync();
        remaining.Class.Should().Be(NotificationClass.Actionable);
    }

    [Fact]
    public async Task GetUnreadBadgeCountsAsync_SplitsByClass()
    {
        var userId = Guid.NewGuid();
        await _repo.AddAsync(CreateNotification(userId, NotificationClass.Actionable));
        await _repo.AddAsync(CreateNotification(userId, NotificationClass.Actionable));
        await _repo.AddAsync(CreateNotification(userId, NotificationClass.Informational));

        var (actionable, informational) = await _repo.GetUnreadBadgeCountsAsync(userId);

        actionable.Should().Be(2);
        informational.Should().Be(1);
    }

    private Notification CreateNotification(
        Guid userId,
        NotificationClass cls = NotificationClass.Informational,
        Instant? createdAt = null)
    {
        var n = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            Source = NotificationSource.TeamMemberAdded,
            Class = cls,
            Priority = NotificationPriority.Normal,
            CreatedAt = createdAt ?? _now,
        };
        n.Recipients.Add(new NotificationRecipient
        {
            NotificationId = n.Id,
            UserId = userId,
        });
        return n;
    }
}
