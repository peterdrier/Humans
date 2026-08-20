using Humans.Base.Caching;
using Humans.Notifications.Data;
using Humans.Notifications.Services;
using AwesomeAssertions;
using static Humans.Notifications.Tests.NotificationTestFixtures;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;


using Humans.Users.Contracts;

namespace Humans.Notifications.Tests.Services;

public class NotificationServiceTests : IDisposable
{
    private readonly PreferenceRegistry _dbContext = new();
    private readonly NotificationsDbContext _notificationsDb;
    private readonly FakeClock _clock;
    private readonly IMemoryCache _cache;
    private readonly NotificationRepository _repo;
    private readonly NotificationService _service;
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly INotificationRecipientResolver _recipientResolver = Substitute.For<INotificationRecipientResolver>();

    public NotificationServiceTests()
    {
        // notifications/notification_recipients live in NotificationsDbContext
        // since the Notifications peel (nobodies-collective/Humans#858);
        // communication_preferences stays on the main pile.
        var notificationsOptions = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _notificationsDb = new NotificationsDbContext(notificationsOptions);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
        _repo = new NotificationRepository(new TestDbContextFactory<NotificationsDbContext>(notificationsOptions));

        // Delegate to in-memory DB so seeded preferences are respected.
        _preferenceService.StubInboxDisabledFrom(_dbContext);

        var emitter = new NotificationEmitter(
            _repo, _preferenceService, _clock, _cache,
            NullLogger<NotificationEmitter>.Instance);
        _service = new NotificationService(
            emitter, _repo, _recipientResolver, _preferenceService,
            _clock, _cache, NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        _notificationsDb.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SendAsync_CreatesOneNotificationPerUser()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Added to team",
            [user1, user2],
            body: "You were added to Logistics",
            actionUrl: "/Teams/logistics", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var notifications = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        notifications.Should().HaveCount(2);
        notifications.Should().AllSatisfy(n =>
        {
            n.Title.Should().Be("Added to team");
            n.Body.Should().Be("You were added to Logistics");
            n.ActionUrl.Should().Be("/Teams/logistics");
            n.Source.Should().Be(NotificationSource.TeamMemberAdded);
            n.Class.Should().Be(NotificationClass.Informational);
            n.Priority.Should().Be(NotificationPriority.Normal);
            n.Recipients.Should().HaveCount(1);
            n.ResolvedAt.Should().BeNull();
        });
    }

    [HumansFact]
    public async Task SendAsync_PersistsActionLabelAndTargetGroupName()
    {
        var userId = Guid.NewGuid();

        await _service.SendAsync(
            NotificationSource.ShiftCoverageGap,
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Coverage gap",
            [userId],
            actionLabel: "Find cover →",
            targetGroupName: "Coordinators", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        notification.ActionLabel.Should().Be("Find cover →");
        notification.TargetGroupName.Should().Be("Coordinators");
    }

    [HumansFact]
    public async Task SendAsync_EmptyRecipientList_DoesNothing()
    {
        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test",
            [], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var count = await _notificationsDb.Notifications.CountAsync(Xunit.TestContext.Current.CancellationToken);
        count.Should().Be(0);
    }

    [HumansFact]
    public async Task SendAsync_SkipsInformationalWhenInboxDisabled()
    {
        var userId = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new()
        {
            UserId = userId,
            Category = MessageCategory.TeamUpdates,
            InboxEnabled = false,
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded, // maps to TeamUpdates
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Added to team",
            [userId], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var count = await _notificationsDb.Notifications.CountAsync(Xunit.TestContext.Current.CancellationToken);
        count.Should().Be(0);
    }

    [HumansFact]
    public async Task SendAsync_ActionableNotSuppressedByInboxDisabled()
    {
        var userId = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new()
        {
            UserId = userId,
            Category = MessageCategory.System,
            InboxEnabled = false,
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SendAsync(
            NotificationSource.ConsentReviewNeeded, // maps to System
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Consent review needed",
            [userId], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var count = await _notificationsDb.Notifications.CountAsync(Xunit.TestContext.Current.CancellationToken);
        count.Should().Be(1);
    }

    [HumansFact]
    public async Task SendToRoleAsync_CreatesSharedNotificationForRoleHolders()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _recipientResolver.GetActiveUserIdsForRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns([user1, user2]);

        await _service.SendToRoleAsync(
            NotificationSource.ApplicationSubmitted,
            NotificationClass.Actionable,
            NotificationPriority.Normal,
            "New tier application submitted",
            "Board",
            actionUrl: "/Governance/BoardVoting", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var notifications = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        notifications.Should().HaveCount(1);
        var notification = notifications.Single();
        notification.TargetGroupName.Should().Be("Board");
        notification.Recipients.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task SendAsync_InvalidatesPerUserBadgeCache()
    {
        var userId = Guid.NewGuid();

        _cache.Set(CacheKeys.NotificationBadgeCounts(userId), new { ActionableUnreadCount = 0, InformationalUnreadCount = 0 });

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test",
            [userId], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _cache.TryGetValue(CacheKeys.NotificationBadgeCounts(userId), out _).Should().BeFalse();

        // Admin nav-badge counts should NOT be affected (they're for admin queues, not notifications).
        _cache.Set(CacheKeys.FeedbackBadgeCount, 1);
        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test2",
            [Guid.NewGuid()], cancellationToken: Xunit.TestContext.Current.CancellationToken);
        _cache.TryGetValue(CacheKeys.FeedbackBadgeCount, out _).Should().BeTrue();
    }
}
