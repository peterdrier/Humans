using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Auth.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Services;
using AwesomeAssertions;
using static Humans.Notifications.Tests.NotificationTestFixtures;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;



namespace Humans.Notifications.Tests.Services;

public class NotificationServiceTests : IDisposable
{
    private readonly PreferenceRegistry _dbContext = new();
    private readonly NotificationsDbContext _notificationsDb;
    private readonly FakeClock _clock;
    private readonly NotificationRepository _repo;
    private readonly NotificationService _service;
    private readonly ICommunicationPreferenceService _preferenceService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();

    public NotificationServiceTests()
    {
        // notifications/notification_recipients live in NotificationsDbContext;
        // communication_preferences rows are seeded into a PreferenceRegistry stand-in.
        var notificationsOptions = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _notificationsDb = new NotificationsDbContext(notificationsOptions);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _repo = new NotificationRepository(new TestDbContextFactory<NotificationsDbContext>(notificationsOptions));

        // Delegate to in-memory DB so seeded preferences are respected.
        _preferenceService.StubInboxDisabledFrom(_dbContext);

        var emitter = new NotificationEmitter(
            _repo, _preferenceService, _clock,
            NullLogger<NotificationEmitter>.Instance);
        _service = new NotificationService(
            emitter, _repo, _roleAssignmentService, _preferenceService,
            _clock, NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        _notificationsDb.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SendAsync_DelegatesToTheEmitter()
    {
        // NotificationService.SendAsync is a pass-through to INotificationEmitter.
        // What SendAsync *does* — per-recipient rows, preference suppression,
        // field persistence, badge freshness — is the emitter's behaviour and
        // is covered once, in NotificationEmitterTests. This asserts only that the
        // delegation is live, so the pass-through cannot silently become a no-op.
        var user = Guid.NewGuid();

        await _service.SendAsync(
            TestSources.TeamUpdate,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Added to team",
            [user], cancellationToken: TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .SingleAsync(TestContext.Current.CancellationToken);

        notification.Title.Should().Be("Added to team");
        notification.Recipients.Single().UserId.Should().Be(user);
    }





    [HumansFact]
    public async Task ReassignAsync_RefreshesBothUsersBadgeCountsAfterMovingRecipients()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await _service.SendAsync(TestSources.TeamUpdate, NotificationClass.Informational,
            NotificationPriority.Normal, "Source notice", [source], cancellationToken: ct);
        await _service.SendAsync(TestSources.TeamUpdate, NotificationClass.Informational,
            NotificationPriority.Normal, "Target notice", [target], cancellationToken: ct);
        var inbox = new NotificationInboxService(_repo, Substitute.For<IUserService>(), TestSources.Registry, _clock);
        (await inbox.GetUnreadBadgeCountsAsync(source, ct)).Should().Be((0, 1));
        (await inbox.GetUnreadBadgeCountsAsync(target, ct)).Should().Be((0, 1));

        await _service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(), ct);

        (await inbox.GetUnreadBadgeCountsAsync(source, ct)).Should().Be((0, 0));
        (await inbox.GetUnreadBadgeCountsAsync(target, ct)).Should().Be((0, 2));
    }

    [HumansFact]
    public async Task ReassignAsync_PropagatesPersistenceFailure()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var failure = new IOException("Save acknowledgement lost");
        var repo = Substitute.For<INotificationRepository>();
        repo.ReassignRecipientsToUserAsync(source, target, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));
        var service = new NotificationService(Substitute.For<INotificationEmitter>(), repo,
            _roleAssignmentService, _preferenceService, _clock, NullLogger<NotificationService>.Instance);

        var act = () => service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
    }

    [HumansFact]
    public async Task SendToRoleAsync_CreatesSharedNotificationForRoleHolders()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns([user1, user2]);

        await _service.SendToRoleAsync(
            TestSources.Approval,
            NotificationClass.Actionable,
            NotificationPriority.Normal,
            "New tier application submitted",
            "Board",
            actionUrl: "/Governance/BoardVoting", cancellationToken: TestContext.Current.CancellationToken);

        var notifications = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .ToListAsync(TestContext.Current.CancellationToken);

        notifications.Should().HaveCount(1);
        var notification = notifications.Single();
        notification.TargetGroupName.Should().Be("Board");
        notification.Recipients.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task SendToRoleAsync_LongCopyFitsStorageAndKeepsOneSharedNotice()
    {
        var holders = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns(holders);
        var title = "Working group applied: " + new string('x', 200);
        var detail = string.Concat(Enumerable.Repeat("🚀", 2000));

        await _service.SendToRoleAsync(TestSources.Approval,
            NotificationClass.Actionable, NotificationPriority.Normal, title, "Board", detail,
            cancellationToken: TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications.Include(n => n.Recipients)
            .SingleAsync(TestContext.Current.CancellationToken);
        notification.Title.Should().Be(string.Concat(title.EnumerateRunes().Take(199)) + "…");
        notification.Body.Should().Be(string.Concat((title + "\n\n" + detail).EnumerateRunes().Take(1999)) + "…");
        notification.TargetGroupName.Should().Be("Board");
        notification.Recipients.Select(r => r.UserId).Should().BeEquivalentTo(holders);
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
            Category = TestSources.TeamUpdate.Category,
            InboxEnabled = false,
        });

        await _service.SendToRoleAsync(
            TestSources.TeamUpdate,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Heads up",
            "Board", cancellationToken: TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .SingleAsync(TestContext.Current.CancellationToken);
        notification.Recipients.Select(r => r.UserId).Should().BeEquivalentTo([allowed]);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendToRoleAsync_Actionable_IgnoresInboxPreference(bool preferenceReadFails)
    {
        var holder = Guid.NewGuid();
        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns([holder]);
        _dbContext.CommunicationPreferences.Add(new()
        {
            UserId = holder,
            Category = TestSources.TeamUpdate.Category,
            InboxEnabled = false,
        });

        if (preferenceReadFails)
            _preferenceService.GetUsersWithInboxDisabledAsync(
                    Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IReadOnlySet<Guid>>(new InvalidOperationException("Preferences unavailable")));

        await _service.SendToRoleAsync(
            TestSources.TeamUpdate,
            NotificationClass.Actionable,
            NotificationPriority.Normal,
            "Act on this",
            "Board", cancellationToken: TestContext.Current.CancellationToken);

        var notification = await _notificationsDb.Notifications
            .Include(n => n.Recipients)
            .SingleAsync(TestContext.Current.CancellationToken);
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
            Category = TestSources.TeamUpdate.Category,
            InboxEnabled = false,
        });

        await _service.SendToRoleAsync(
            TestSources.TeamUpdate,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            "Heads up",
            "Board", cancellationToken: TestContext.Current.CancellationToken);

        (await _notificationsDb.Notifications.AnyAsync(TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }
}
