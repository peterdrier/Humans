using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using NotificationService = Humans.Application.Services.Notifications.NotificationService;
using NotificationEmitter = Humans.Application.Services.Notifications.NotificationEmitter;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Tests.Services;

public class NotificationServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IMemoryCache _cache;
    private readonly NotificationRepository _repo;
    private readonly NotificationService _service;
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly INotificationRecipientResolver _recipientResolver = Substitute.For<INotificationRecipientResolver>();

    public NotificationServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
        _repo = new NotificationRepository(new TestDbContextFactory(options));

        // Delegate to in-memory DB so seeded preferences are respected.
        _preferenceService.GetUsersWithInboxDisabledAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userIds = callInfo.Arg<IReadOnlyList<Guid>>();
                var category = callInfo.Arg<MessageCategory>();
                var disabledIds = _dbContext.CommunicationPreferences
                    .Where(cp => userIds.Contains(cp.UserId) && cp.Category == category && !cp.InboxEnabled)
                    .Select(cp => cp.UserId)
                    .ToHashSet();
                return Task.FromResult<IReadOnlySet<Guid>>(disabledIds);
            });

        var emitter = new NotificationEmitter(
            _repo, _preferenceService, _clock, _cache,
            NullLogger<NotificationEmitter>.Instance);
        _service = new NotificationService(
            emitter, _repo, _recipientResolver, _preferenceService,
            _clock, _cache, NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
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
            actionUrl: "/Teams/logistics");

        var notifications = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .ToListAsync();

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

    [Fact]
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
            targetGroupName: "Coordinators");

        var notification = await _dbContext.Notifications.SingleAsync();
        notification.ActionLabel.Should().Be("Find cover →");
        notification.TargetGroupName.Should().Be("Coordinators");
    }

    [Fact]
    public async Task SendAsync_EmptyRecipientList_DoesNothing()
    {
        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test",
            []);

        var count = await _dbContext.Notifications.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_SkipsInformationalWhenInboxDisabled()
    {
        var userId = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = MessageCategory.TeamUpdates,
            InboxEnabled = false,
            UpdatedAt = _clock.GetCurrentInstant(),
            UpdateSource = "Test"
        });
        await _dbContext.SaveChangesAsync();

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded, // maps to TeamUpdates
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Added to team",
            [userId]);

        var count = await _dbContext.Notifications.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ActionableNotSuppressedByInboxDisabled()
    {
        var userId = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = MessageCategory.System,
            InboxEnabled = false,
            UpdatedAt = _clock.GetCurrentInstant(),
            UpdateSource = "Test"
        });
        await _dbContext.SaveChangesAsync();

        await _service.SendAsync(
            NotificationSource.ConsentReviewNeeded, // maps to System
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Consent review needed",
            [userId]);

        var count = await _dbContext.Notifications.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task SendToTeamAsync_CreatesSharedNotification()
    {
        var teamId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _recipientResolver.GetTeamNotificationInfoAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new TeamNotificationInfo(teamId, "Logistics", [user1, user2]));

        await _service.SendToTeamAsync(
            NotificationSource.ShiftCoverageGap,
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Coverage gap: Saturday 10:00-14:00",
            teamId,
            actionUrl: "/Shifts/Dashboard");

        var notifications = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .ToListAsync();

        notifications.Should().HaveCount(1);
        var notification = notifications.Single();
        notification.TargetGroupName.Should().Be("Logistics");
        notification.Recipients.Should().HaveCount(2);
        notification.Recipients.Select(r => r.UserId).Should().Contain(user1);
        notification.Recipients.Select(r => r.UserId).Should().Contain(user2);
    }

    [Fact]
    public async Task SendToRoleAsync_CreatesSharedNotificationForRoleHolders()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _recipientResolver.GetActiveUserIdsForRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Guid>)[user1, user2]);

        await _service.SendToRoleAsync(
            NotificationSource.ApplicationSubmitted,
            NotificationClass.Actionable,
            NotificationPriority.Normal,
            "New tier application submitted",
            "Board",
            actionUrl: "/OnboardingReview/BoardVoting");

        var notifications = await _dbContext.Notifications
            .Include(n => n.Recipients)
            .ToListAsync();

        notifications.Should().HaveCount(1);
        var notification = notifications.Single();
        notification.TargetGroupName.Should().Be("Board");
        notification.Recipients.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendAsync_InvalidatesPerUserBadgeCache()
    {
        var userId = Guid.NewGuid();

        _cache.Set(CacheKeys.NotificationBadgeCounts(userId), new { ActionableUnreadCount = 0, InformationalUnreadCount = 0 });

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test",
            [userId]);

        _cache.TryGetValue(CacheKeys.NotificationBadgeCounts(userId), out _).Should().BeFalse();

        // Global NavBadgeCounts should NOT be affected (it's for admin queues, not notifications).
        _cache.Set(CacheKeys.NavBadgeCounts, (Review: 1, Voting: 2, Feedback: 0));
        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test2",
            [Guid.NewGuid()]);
        _cache.TryGetValue(CacheKeys.NavBadgeCounts, out _).Should().BeTrue();
    }
}
