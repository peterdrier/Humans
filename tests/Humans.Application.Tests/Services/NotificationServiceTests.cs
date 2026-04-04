using AwesomeAssertions;
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

public class NotificationServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IMemoryCache _cache;
    private readonly NotificationService _service;

    public NotificationServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new NotificationService(_dbContext, _clock, _cache, NullLogger<NotificationService>.Instance);
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
            actionLabel: "Find cover \u2192",
            targetGroupName: "Coordinators");

        var notification = await _dbContext.Notifications.SingleAsync();
        notification.ActionLabel.Should().Be("Find cover \u2192");
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

        // Create preference with InboxEnabled = false
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

        // Create preference with InboxEnabled = false
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

        _dbContext.Teams.Add(new Team { Id = teamId, Name = "Logistics", Slug = "logistics" });
        _dbContext.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), TeamId = teamId, UserId = user1, Role = TeamMemberRole.Member });
        _dbContext.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), TeamId = teamId, UserId = user2, Role = TeamMemberRole.Member });
        await _dbContext.SaveChangesAsync();

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

        // One shared notification for the team
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
        var now = _clock.GetCurrentInstant();

        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user1,
            RoleName = "Board",
            ValidFrom = now - Duration.FromDays(30),
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user2,
            RoleName = "Board",
            ValidFrom = now - Duration.FromDays(30),
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        // Expired role - should NOT be included
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RoleName = "Board",
            ValidFrom = now - Duration.FromDays(60),
            ValidTo = now - Duration.FromDays(10),
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        await _dbContext.SaveChangesAsync();

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

        // Seed the per-user notification badge cache
        _cache.Set(CacheKeys.NotificationBadgeCounts(userId), new { ActionableUnreadCount = 0, InformationalUnreadCount = 0 });

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Test",
            [userId]);

        // Per-user cache should be evicted
        _cache.TryGetValue(CacheKeys.NotificationBadgeCounts(userId), out _).Should().BeFalse();

        // Global NavBadgeCounts should NOT be affected (it's for admin queues, not notifications)
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
