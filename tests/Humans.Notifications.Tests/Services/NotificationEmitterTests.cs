using Humans.Application;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Notifications.Data;
using Humans.Notifications.Services;
using AwesomeAssertions;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Notifications.Tests;
using static Humans.Notifications.Tests.NotificationTestFixtures;
using Humans.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;


namespace Humans.Notifications.Tests.Services;

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
    private readonly PreferenceRegistry _dbContext = new();
    private readonly NotificationsDbContext _notificationsDb;
    private readonly FakeClock _clock;
    private readonly IMemoryCache _cache;
    private readonly NotificationRepository _repo;
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly NotificationEmitter _emitter;

    public NotificationEmitterTests()
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

        _preferenceService.StubInboxDisabledFrom(_dbContext);

        _emitter = new NotificationEmitter(
            _repo, _preferenceService, _clock, _cache,
            NullLogger<NotificationEmitter>.Instance);
    }

    public void Dispose()
    {
        _notificationsDb.Dispose();
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
            recipientUserIds: [], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        (await _notificationsDb.Notifications.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
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
            recipientUserIds: [user1, user2], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var stored = await _notificationsDb.Notifications
            .AsNoTracking()
            .Include(n => n.Recipients)
            .OrderBy(n => n.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

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
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Filtered",
            recipientUserIds: [suppressed, allowed], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var rows = await _notificationsDb.NotificationRecipients.AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken);
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
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _emitter.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "All suppressed",
            recipientUserIds: [u1, u2], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        (await _notificationsDb.Notifications.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
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
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _emitter.SendAsync(
            NotificationSource.ApplicationSubmitted,
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Action required",
            recipientUserIds: [suppressed], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var rows = await _notificationsDb.NotificationRecipients.AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken);
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
            targetGroupName: "Build Team", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var n = await _notificationsDb.Notifications.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
            recipientUserIds: [user], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _cache.TryGetValue(key, out _).Should().BeFalse();
    }
}
