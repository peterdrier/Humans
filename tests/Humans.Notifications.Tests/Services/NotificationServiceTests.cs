using Humans.Auth.Contracts;
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
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();

    public NotificationServiceTests()
    {
        // notifications/notification_recipients live in NotificationsDbContext;
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
            emitter, _repo, _roleAssignmentService, _preferenceService,
            _clock, _cache, NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        _notificationsDb.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SendAsync_DelegatesToTheEmitter()
    {
        // NotificationService.SendAsync is a pass-through to INotificationEmitter.
        // What SendAsync *does* — per-recipient rows, preference suppression,
        // field persistence, badge-cache eviction — is the emitter's behaviour and
        // is covered once, in NotificationEmitterTests. This asserts only that the
        // delegation is live, so the pass-through cannot silently become a no-op.
        var user = Guid.NewGuid();

        await _service.SendAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Added to team",
            [user], cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);

        notification.Title.Should().Be("Added to team");
        notification.Recipients.Single().UserId.Should().Be(user);
    }





    [HumansFact]
    public async Task SendToRoleAsync_CreatesSharedNotificationForRoleHolders()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
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
    public async Task SendToRoleAsync_Informational_SkipsHoldersWithInboxDisabled()
    {
        var suppressed = Guid.NewGuid();
        var allowed = Guid.NewGuid();
        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns([suppressed, allowed]);
        _dbContext.CommunicationPreferences.Add(new()
        {
            UserId = suppressed,
            Category = NotificationSource.TeamMemberAdded.ToMessageCategory(),
            InboxEnabled = false,
        });

        await _service.SendToRoleAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Heads up",
            "Board", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        notification.Recipients.Select(r => r.UserId).Should().BeEquivalentTo([allowed]);
    }

    [HumansFact]
    public async Task SendToRoleAsync_Actionable_IgnoresInboxPreference()
    {
        var holder = Guid.NewGuid();
        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns([holder]);
        _dbContext.CommunicationPreferences.Add(new()
        {
            UserId = holder,
            Category = NotificationSource.TeamMemberAdded.ToMessageCategory(),
            InboxEnabled = false,
        });

        await _service.SendToRoleAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Actionable,
            NotificationPriority.Normal,
            "Act on this",
            "Board", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        notification.Recipients.Single().UserId.Should().Be(holder);
    }

    [HumansFact]
    public async Task SendToRoleAsync_AllHoldersSuppressed_WritesNothing()
    {
        var holder = Guid.NewGuid();
        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns([holder]);
        _dbContext.CommunicationPreferences.Add(new()
        {
            UserId = holder,
            Category = NotificationSource.TeamMemberAdded.ToMessageCategory(),
            InboxEnabled = false,
        });

        await _service.SendToRoleAsync(
            NotificationSource.TeamMemberAdded,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Heads up",
            "Board", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        (await _notificationsDb.Notifications.AnyAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }
}
