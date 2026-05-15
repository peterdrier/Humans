using AwesomeAssertions;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NotificationEmitter = Humans.Application.Services.Notifications.NotificationEmitter;

namespace Humans.Application.Tests.Notifications;

/// <summary>
/// Direct unit tests for <see cref="NotificationEmitter"/>, the narrow
/// recipient-known dispatch surface that <see cref="INotificationEmitter"/>
/// resolves to. The emitter is a separate concrete from
/// <c>NotificationService</c> so that team / role-assignment services can
/// inject the narrower interface without closing a DI cycle through
/// <see cref="INotificationRecipientResolver"/>.
/// </summary>
public class NotificationEmitterTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IMemoryCache _cache;
    private readonly NotificationRepository _repo;
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly NotificationEmitter _emitter;

    public NotificationEmitterTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
        _repo = new NotificationRepository(new TestDbContextFactory(options));

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

        _emitter = new NotificationEmitter(
            _repo, _preferenceService, _clock, _cache,
            NullLogger<NotificationEmitter>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SendAsync_EmptyRecipientList_WritesNothing()
    {
        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Empty",
            recipientUserIds: []);

        (await _dbContext.Notifications.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task SendAsync_CreatesOneNotificationPerRecipient_IndividualScope()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Hello",
            recipientUserIds: [user1, user2]);

        var stored = await _dbContext.Notifications
            .AsNoTracking()
            .Include(n => n.Recipients)
            .OrderBy(n => n.Id)
            .ToListAsync();

        stored.Should().HaveCount(2);
        stored.Should().OnlyContain(n => n.Recipients.Count == 1);
        stored.SelectMany(n => n.Recipients).Select(r => r.UserId)
            .Should().BeEquivalentTo([user1, user2]);
    }

    [HumansFact]
    public async Task SendAsync_Informational_SuppressedRecipientsAreSkipped()
    {
        var suppressed = Guid.NewGuid();
        var allowed = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            UserId = suppressed,
            Category = NotificationSource.TeamMemberAdded.ToMessageCategory(),
            InboxEnabled = false,
        });
        await _dbContext.SaveChangesAsync();

        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Filtered",
            recipientUserIds: [suppressed, allowed]);

        var rows = await _dbContext.NotificationRecipients.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows.Single().UserId.Should().Be(allowed);
    }

    [HumansFact]
    public async Task SendAsync_AllRecipientsSuppressed_WritesNothing()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            UserId = u1,
            Category = NotificationSource.TeamMemberAdded.ToMessageCategory(),
            InboxEnabled = false,
        });
        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            UserId = u2,
            Category = NotificationSource.TeamMemberAdded.ToMessageCategory(),
            InboxEnabled = false,
        });
        await _dbContext.SaveChangesAsync();

        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "All suppressed",
            recipientUserIds: [u1, u2]);

        (await _dbContext.Notifications.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task SendAsync_Actionable_BypassesInboxSuppression()
    {
        var suppressed = Guid.NewGuid();

        _dbContext.CommunicationPreferences.Add(new CommunicationPreference
        {
            UserId = suppressed,
            Category = NotificationSource.ApplicationSubmitted.ToMessageCategory(),
            InboxEnabled = false,
        });
        await _dbContext.SaveChangesAsync();

        await _emitter.SendAsync(
            NotificationSource.ApplicationSubmitted,
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Action required",
            recipientUserIds: [suppressed]);

        var rows = await _dbContext.NotificationRecipients.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle(r => r.UserId == suppressed);
    }

    [HumansFact]
    public async Task SendAsync_Persists_AllProvidedFields()
    {
        var user = Guid.NewGuid();

        await _emitter.SendAsync(
            NotificationSource.ShiftSignupChange,
            NotificationClass.Actionable,
            NotificationPriority.Critical,
            "Title",
            recipientUserIds: [user],
            body: "Body text",
            actionUrl: "/somewhere",
            actionLabel: "Open",
            targetGroupName: "Build Team");

        var n = await _dbContext.Notifications.AsNoTracking().SingleAsync();
        n.Title.Should().Be("Title");
        n.Body.Should().Be("Body text");
        n.ActionUrl.Should().Be("/somewhere");
        n.ActionLabel.Should().Be("Open");
        n.Source.Should().Be(NotificationSource.ShiftSignupChange);
        n.Class.Should().Be(NotificationClass.Actionable);
        n.Priority.Should().Be(NotificationPriority.Critical);
        n.TargetGroupName.Should().Be("Build Team");
        n.CreatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SendAsync_InvalidatesPerRecipientBadgeCache()
    {
        var user = Guid.NewGuid();
        var key = CacheKeys.NotificationBadgeCounts(user);
        _cache.Set(key, (Actionable: 1, Informational: 2));

        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Cache evict",
            recipientUserIds: [user]);

        _cache.TryGetValue(key, out _).Should().BeFalse();
    }
}
