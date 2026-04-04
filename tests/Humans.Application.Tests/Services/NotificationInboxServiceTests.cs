using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

public class NotificationInboxServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IMemoryCache _cache;
    private readonly NotificationInboxService _service;
    private readonly Guid _userId = Guid.NewGuid();

    public NotificationInboxServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new NotificationInboxService(
            _dbContext, _clock, _cache,
            NullLogger<NotificationInboxService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Notification> CreateNotification(
        NotificationClass cls = NotificationClass.Informational,
        NotificationSource source = NotificationSource.TeamMemberAdded,
        Instant? resolvedAt = null,
        Guid? resolvedByUserId = null)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Test Notification",
            Body = "Test body",
            ActionUrl = "/test",
            Priority = NotificationPriority.Normal,
            Source = source,
            Class = cls,
            CreatedAt = _clock.GetCurrentInstant(),
            ResolvedAt = resolvedAt,
            ResolvedByUserId = resolvedByUserId,
        };

        notification.Recipients.Add(new NotificationRecipient
        {
            NotificationId = notification.Id,
            UserId = _userId,
        });

        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();
        return notification;
    }

    // --- ResolveAsync ---

    [Fact]
    public async Task ResolveAsync_ResolvesNotification()
    {
        var notification = await CreateNotification(NotificationClass.Actionable);

        var result = await _service.ResolveAsync(notification.Id, _userId);

        result.Success.Should().BeTrue();

        var updated = await _dbContext.Notifications.FindAsync(notification.Id);
        updated!.ResolvedAt.Should().NotBeNull();
        updated.ResolvedByUserId.Should().Be(_userId);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNotFoundForMissingNotification()
    {
        var result = await _service.ResolveAsync(Guid.NewGuid(), _userId);

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ReturnsForbiddenIfNotRecipient()
    {
        var notification = await CreateNotification(NotificationClass.Actionable);

        var result = await _service.ResolveAsync(notification.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Forbidden.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_IdempotentIfAlreadyResolved()
    {
        var now = _clock.GetCurrentInstant();
        var notification = await CreateNotification(
            NotificationClass.Actionable,
            resolvedAt: now,
            resolvedByUserId: _userId);

        var result = await _service.ResolveAsync(notification.Id, _userId);

        result.Success.Should().BeTrue();
    }

    // --- DismissAsync ---

    [Fact]
    public async Task DismissAsync_DismissesInformationalNotification()
    {
        var notification = await CreateNotification(NotificationClass.Informational);

        var result = await _service.DismissAsync(notification.Id, _userId);

        result.Success.Should().BeTrue();

        var updated = await _dbContext.Notifications.FindAsync(notification.Id);
        updated!.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DismissAsync_RejectsDismissOfActionableNotification()
    {
        var notification = await CreateNotification(NotificationClass.Actionable);

        var result = await _service.DismissAsync(notification.Id, _userId);

        result.Success.Should().BeFalse();
        result.Forbidden.Should().BeTrue();
    }

    [Fact]
    public async Task DismissAsync_ReturnsNotFoundForMissing()
    {
        var result = await _service.DismissAsync(Guid.NewGuid(), _userId);

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
    }

    // --- MarkReadAsync ---

    [Fact]
    public async Task MarkReadAsync_SetsReadAt()
    {
        var notification = await CreateNotification();

        var result = await _service.MarkReadAsync(notification.Id, _userId);

        result.Success.Should().BeTrue();

        var recipient = await _dbContext.NotificationRecipients
            .FirstAsync(nr => nr.NotificationId == notification.Id && nr.UserId == _userId);
        recipient.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkReadAsync_ReturnsNotFoundIfNotRecipient()
    {
        var notification = await CreateNotification();

        var result = await _service.MarkReadAsync(notification.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
    }

    // --- MarkAllReadAsync ---

    [Fact]
    public async Task MarkAllReadAsync_MarksAllUnreadAsRead()
    {
        await CreateNotification();
        await CreateNotification();

        await _service.MarkAllReadAsync(_userId);

        var unread = await _dbContext.NotificationRecipients
            .Where(nr => nr.UserId == _userId && nr.ReadAt == null)
            .CountAsync();
        unread.Should().Be(0);
    }

    // --- BulkResolveAsync ---

    [Fact]
    public async Task BulkResolveAsync_ResolvesMultipleActionableNotifications()
    {
        var n1 = await CreateNotification(NotificationClass.Actionable);
        var n2 = await CreateNotification(NotificationClass.Actionable);

        await _service.BulkResolveAsync([n1.Id, n2.Id], _userId);

        var resolved = await _dbContext.Notifications
            .Where(n => n.ResolvedAt != null)
            .CountAsync();
        resolved.Should().Be(2);
    }

    [Fact]
    public async Task BulkResolveAsync_SkipsInformationalNotifications()
    {
        var actionable = await CreateNotification(NotificationClass.Actionable);
        var informational = await CreateNotification(NotificationClass.Informational);

        await _service.BulkResolveAsync([actionable.Id, informational.Id], _userId);

        var resolvedActionable = await _dbContext.Notifications.FindAsync(actionable.Id);
        resolvedActionable!.ResolvedAt.Should().NotBeNull();

        var unresolvedInfo = await _dbContext.Notifications.FindAsync(informational.Id);
        unresolvedInfo!.ResolvedAt.Should().BeNull();
    }

    // --- BulkDismissAsync ---

    [Fact]
    public async Task BulkDismissAsync_DismissesMultipleInformationalNotifications()
    {
        var n1 = await CreateNotification(NotificationClass.Informational);
        var n2 = await CreateNotification(NotificationClass.Informational);

        await _service.BulkDismissAsync([n1.Id, n2.Id], _userId);

        var resolved = await _dbContext.Notifications
            .Where(n => n.ResolvedAt != null)
            .CountAsync();
        resolved.Should().Be(2);
    }

    [Fact]
    public async Task BulkDismissAsync_SkipsActionableNotifications()
    {
        var actionable = await CreateNotification(NotificationClass.Actionable);
        var informational = await CreateNotification(NotificationClass.Informational);

        await _service.BulkDismissAsync([actionable.Id, informational.Id], _userId);

        var unresolvedActionable = await _dbContext.Notifications.FindAsync(actionable.Id);
        unresolvedActionable!.ResolvedAt.Should().BeNull();

        var resolvedInfo = await _dbContext.Notifications.FindAsync(informational.Id);
        resolvedInfo!.ResolvedAt.Should().NotBeNull();
    }

    // --- ClickThroughAsync ---

    [Fact]
    public async Task ClickThroughAsync_MarksReadAndReturnsUrl()
    {
        var notification = await CreateNotification();

        var url = await _service.ClickThroughAsync(notification.Id, _userId);

        url.Should().Be("/test");

        var recipient = await _dbContext.NotificationRecipients
            .FirstAsync(nr => nr.NotificationId == notification.Id && nr.UserId == _userId);
        recipient.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ClickThroughAsync_ReturnsNullIfNotRecipient()
    {
        var notification = await CreateNotification();

        var url = await _service.ClickThroughAsync(notification.Id, Guid.NewGuid());

        url.Should().BeNull();
    }

    // --- GetPopupAsync ---

    [Fact]
    public async Task GetPopupAsync_ReturnsUnresolvedNotificationsSplitByClass()
    {
        await CreateNotification(NotificationClass.Actionable);
        await CreateNotification(NotificationClass.Informational);
        await CreateNotification(
            NotificationClass.Actionable,
            resolvedAt: _clock.GetCurrentInstant(),
            resolvedByUserId: _userId); // resolved — should not appear

        var result = await _service.GetPopupAsync(_userId);

        result.Actionable.Should().HaveCount(1);
        result.Informational.Should().HaveCount(1);
        result.ActionableCount.Should().Be(1);
    }

    // --- Cache invalidation ---

    [Fact]
    public async Task ResolveAsync_InvalidatesBadgeCache()
    {
        var notification = await CreateNotification(NotificationClass.Actionable);
        var cacheKey = Application.CacheKeys.NotificationBadgeCounts(_userId);
        _cache.Set(cacheKey, 5);

        await _service.ResolveAsync(notification.Id, _userId);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task MarkReadAsync_InvalidatesBadgeCache()
    {
        var notification = await CreateNotification();
        var cacheKey = Application.CacheKeys.NotificationBadgeCounts(_userId);
        _cache.Set(cacheKey, 3);

        await _service.MarkReadAsync(notification.Id, _userId);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task MarkAllReadAsync_InvalidatesBadgeCache()
    {
        await CreateNotification();
        var cacheKey = Application.CacheKeys.NotificationBadgeCounts(_userId);
        _cache.Set(cacheKey, 2);

        await _service.MarkAllReadAsync(_userId);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }
}
