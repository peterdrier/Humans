using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Campaigns.Contracts;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Email.Services;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Base.Services.Metering;
using Humans.Settings.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Email.Tests.Services;

/// <summary>
/// The outbox drain. It moved out of <c>ProcessEmailOutboxJob</c> and behind
/// <see cref="IEmailOutboxProcessor"/> at the section's G5 move (design §15 step 6b) —
/// what is left of the job is one call plus a metric.
/// </summary>
public class EmailOutboxProcessorTests : IDisposable
{
    private readonly DbContextOptions<EmailDbContext> _options;
    private readonly EmailDbContext _dbContext;
    private readonly IEmailTransport _transport;
    private readonly ICampaignService _campaignService;
    private readonly FakeClock _clock;
    // Substituted rather than built: the concrete HumansMetricsService moved to Humans.Web at
    // G5 lane 5b-6 and this section may not reference it. Nothing here asserts on metrics —
    // OutboxEmailServiceTests already used the substitute.
    private readonly IHumansMetrics _metrics;
    private readonly MetersService _meters;
    private readonly IOptions<EmailSettings> _settings;
    private readonly EmailOutboxRepository _repo;
    private readonly ISettingsService _settingsStore;
    private readonly EmailOutboxService _outboxService;
    private readonly IEmailOutboxProcessor _job;

    public EmailOutboxProcessorTests()
    {
        _options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new EmailDbContext(_options);
        _transport = Substitute.For<IEmailTransport>();
        _campaignService = Substitute.For<ICampaignService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 14, 12, 0));
        _metrics = Substitute.For<IHumansMetrics>();
        _meters = new MetersService(Substitute.For<ILogger<MetersService>>());
        _settings = Options.Create(new EmailSettings { OutboxBatchSize = 10, OutboxMaxRetries = 10 });
        _repo = new EmailOutboxRepository(new TestDbContextFactory<EmailDbContext>(_options));
        // Settings is its own section (#866 G5): its repository, service and
        // DbContext are internal to it, so this job test substitutes the contract.
        _settingsStore = Substitute.For<ISettingsService>();
        _outboxService = new EmailOutboxService(_repo, _settingsStore, _settings, _clock);

        _job = NewProcessor(_settings);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _meters.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact(Timeout = 10000)]
    public async Task ProcessQueuedAsync_ProcessesQueuedMessages()
    {
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.Received(1).SendAsync(
            message.RecipientEmail,
            message.RecipientName,
            message.Subject,
            message.HtmlBody,
            message.PlainTextBody,
            message.ReplyTo,
            Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Sent);
        updated.SentAt.Should().Be(_clock.GetCurrentInstant());
        updated.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_HandlesFailure()
    {
        await SeedMessageAsync(EmailOutboxStatus.Queued);

        _transport.SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP timeout"));

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Failed);
        updated.RetryCount.Should().Be(1);
        updated.LastError.Should().Contain("SMTP timeout");
        updated.NextRetryAt.Should().NotBeNull();
        updated.PickedUpAt.Should().BeNull();

        // Exponential backoff: 2^1 = 2 minutes
        var expectedRetryAt = _clock.GetCurrentInstant() + Duration.FromMinutes(2);
        updated.NextRetryAt.Should().Be(expectedRetryAt);
    }

    [HumansFact(Timeout = 30000)]
    public async Task ProcessQueuedAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 15; i++)
            await SeedMessageAsync(EmailOutboxStatus.Queued);

        var batchSettings = Options.Create(new EmailSettings { OutboxBatchSize = 10, OutboxMaxRetries = 10 });
        var job = NewProcessor(batchSettings);

        await job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.Received(10).SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_SkipsPaused()
    {
        _settingsStore
            .GetValueAsync(SettingKeys.IsEmailSendingPaused, Arg.Any<CancellationToken>())
            .Returns("true");

        await SeedMessageAsync(EmailOutboxStatus.Queued);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact(Timeout = 10000)]
    public async Task ProcessQueuedAsync_CrashRecovery()
    {
        // Message picked up 6 minutes ago but never completed (simulates crash)
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.PickedUpAt = _clock.GetCurrentInstant() - Duration.FromMinutes(6);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.Received(1).SendAsync(
            message.RecipientEmail,
            Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Sent);
        updated.SentAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_SkipsRecentlyPickedUp()
    {
        // Message picked up 2 minutes ago — still within the 5 minute window
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.PickedUpAt = _clock.GetCurrentInstant() - Duration.FromMinutes(2);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact(Timeout = 10000)]
    public async Task ProcessQueuedAsync_RetriesFailedWithBackoff()
    {
        // Failed message with RetryCount=3, NextRetryAt in the past
        var message = await SeedMessageAsync(EmailOutboxStatus.Failed);
        message.RetryCount = 3;
        message.NextRetryAt = _clock.GetCurrentInstant() - Duration.FromMinutes(1);
        message.LastError = "previous error";
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.Received(1).SendAsync(
            message.RecipientEmail,
            Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Sent);
        updated.SentAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact(Timeout = 10000)]
    public async Task ProcessQueuedAsync_IncrementsDailySentCountOnSuccess()
    {
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var row = await FreshCountsQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.Date.Should().Be(_clock.GetCurrentInstant().InUtc().Date);
        row.TemplateName.Should().Be(message.TemplateName);
        row.SentCount.Should().Be(1);
        row.FailedCount.Should().Be(0);
    }

    [HumansFact(Timeout = 10000)]
    public async Task ProcessQueuedAsync_GrantStatusUpdateFailure_DoesNotDoubleTallyDelivery()
    {
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.CampaignGrantId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        _campaignService.UpdateGrantEmailStatusAsync(
            message.CampaignGrantId.Value, EmailOutboxStatus.Sent, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("grant update failed"));

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        // Transport delivered — the message stays Sent, not re-tallied as Failed
        // just because the post-delivery grant mirror threw.
        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Sent);

        var row = await FreshCountsQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.SentCount.Should().Be(1);
        row.FailedCount.Should().Be(0);
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_GrantStatusUpdateFailureAfterADeliveryFailure_DoesNotAbortTheDrain()
    {
        // The failure-path grant mirror runs inside the per-message catch, so an
        // uncaught throw there has nowhere left to land: it escapes the loop and
        // ProcessQueuedAsync itself, skipping the error log and leaving the rest of
        // the batch stamped PickedUp until the stale window releases it.
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.CampaignGrantId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        _transport.SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP timeout"));

        _campaignService.UpdateGrantEmailStatusAsync(
            message.CampaignGrantId.Value, EmailOutboxStatus.Failed, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("grant update failed"));

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Failed);

        var row = await FreshCountsQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.SentCount.Should().Be(0);
        row.FailedCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_IncrementsDailyFailedCountOnFailure()
    {
        await SeedMessageAsync(EmailOutboxStatus.Queued);
        _transport.SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP timeout"));

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var row = await FreshCountsQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.SentCount.Should().Be(0);
        row.FailedCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_TallyFailureDoesNotFlipDeliveredMessageToFailed()
    {
        // A daily-count write failure is analytics-layer noise, not a delivery
        // failure — it must never flip an already-sent message back to Failed
        // and queue it for a duplicate resend (Codex #1758 finding).
        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = "test@example.com",
            Subject = "Test Subject",
            HtmlBody = "<p>Hello</p>",
            TemplateName = "test_template",
            Status = EmailOutboxStatus.Queued,
            CreatedAt = _clock.GetCurrentInstant() - Duration.FromMinutes(10)
        };

        var repo = Substitute.For<IEmailOutboxRepository>();
        repo.GetProcessingBatchAsync(
                Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        repo.MarkSentAsync(message.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns(true);
        repo.IncrementDailySendCountAsync(
                Arg.Any<LocalDate>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("daily count write failed"));

        var processor = WithoutThrottle(new EmailOutboxProcessor(
            repo, _outboxService, _campaignService, _transport, _metrics, _meters, _clock, _settings,
            NullLogger<EmailOutboxProcessor>.Instance));

        await processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await repo.Received(1).MarkSentAsync(message.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().MarkFailedAsync(
            Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact(Timeout = 10000)]
    public async Task ProcessQueuedAsync_TalliesEachOutcomeUnderItsOwnClockRead_NotBatchStartTime()
    {
        // Batch starts just before midnight UTC; the throttle delay between sends
        // pushes later messages in the same batch past midnight. Each outcome must
        // be tallied under the day it actually landed on, not the batch's start
        // day (Codex #1758 finding).
        var clock = new FakeClock(Instant.FromUtc(2026, 3, 14, 23, 59, 0))
        {
            AutoAdvance = Duration.FromSeconds(40)
        };
        var outboxService = new EmailOutboxService(_repo, _settingsStore, _settings, clock);
        var processor = WithoutThrottle(new EmailOutboxProcessor(
            _repo, outboxService, _campaignService, _transport, _metrics, _meters, clock, _settings,
            NullLogger<EmailOutboxProcessor>.Instance));

        var message1 = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = "a@example.com",
            Subject = "Subject",
            HtmlBody = "<p>Hi</p>",
            TemplateName = "test_template",
            Status = EmailOutboxStatus.Queued,
            CreatedAt = Instant.FromUtc(2026, 3, 14, 23, 0, 0)
        };
        var message2 = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = "b@example.com",
            Subject = "Subject",
            HtmlBody = "<p>Hi</p>",
            TemplateName = "test_template",
            Status = EmailOutboxStatus.Queued,
            CreatedAt = Instant.FromUtc(2026, 3, 14, 23, 0, 0)
        };
        await _dbContext.EmailOutboxMessages.AddRangeAsync(
            [message1, message2], Xunit.TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var rows = await FreshCountsQuery().OrderBy(r => r.Date).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        rows.Should().HaveCount(2);
        rows[0].Date.Should().Be(new LocalDate(2026, 3, 14));
        rows[1].Date.Should().Be(new LocalDate(2026, 3, 15));
        rows.Sum(r => r.SentCount).Should().Be(2);
    }

    [HumansTheory]
    [InlineData("skipped@localhost")]
    [InlineData("skipped@ticketstub.local")]
    public async Task ProcessQueuedAsync_MarksTestAddressesSentWithoutSendingOrCounting(string testAddress)
    {
        // A test-domain row is marked Sent deliberately, without a transport call:
        // sending to it bounces and costs sender reputation. It leaves the queue for
        // good (never retried) and stays out of the tally, which counts real mail.
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.RecipientEmail = testAddress;
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var updated = await FreshQuery().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(EmailOutboxStatus.Sent);

        await _transport.DidNotReceiveWithAnyArgs().SendAsync(
            default!, default, default!, default!, default, default, default, default);

        var counts = await FreshCountsQuery().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        counts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_SkipsFutureRetry()
    {
        // Failed message with NextRetryAt in the future — should not be processed
        var message = await SeedMessageAsync(EmailOutboxStatus.Failed);
        message.RetryCount = 2;
        message.NextRetryAt = _clock.GetCurrentInstant() + Duration.FromMinutes(10);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _job.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    // The repository uses IDbContextFactory to create short-lived contexts that
    // commit changes to the shared InMemory store; the long-lived _dbContext
    // here still tracks the seeded entity and returns its stale state on a
    // straight `ToListAsync`. Route post-job asserts through AsNoTracking on a
    // fresh context from the same factory so we read the updated row.
    private IQueryable<EmailOutboxMessage> FreshQuery()
    {
        var ctx = new EmailDbContext(_options);
        return ctx.EmailOutboxMessages.AsNoTracking();
    }

    private IQueryable<EmailDailySendCount> FreshCountsQuery()
    {
        var ctx = new EmailDbContext(_options);
        return ctx.EmailDailySendCounts.AsNoTracking();
    }

    private EmailOutboxProcessor NewProcessor(IOptions<EmailSettings> settings) =>
        WithoutThrottle(new EmailOutboxProcessor(
            _repo, _outboxService, _campaignService, _transport, _metrics, _meters, _clock, settings,
            NullLogger<EmailOutboxProcessor>.Instance));

    private static EmailOutboxProcessor WithoutThrottle(EmailOutboxProcessor processor)
    {
        processor.ThrottleDelayAsync = static (_, _) => Task.CompletedTask;
        return processor;
    }

    private async Task<EmailOutboxMessage> SeedMessageAsync(EmailOutboxStatus status)
    {
        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = "test@example.com",
            RecipientName = "Test User",
            Subject = "Test Subject",
            HtmlBody = "<p>Hello</p>",
            PlainTextBody = "Hello",
            TemplateName = "test_template",
            ReplyTo = "reply@example.com",
            Status = status,
            CreatedAt = _clock.GetCurrentInstant() - Duration.FromMinutes(10)
        };

        _dbContext.EmailOutboxMessages.Add(message);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return message;
    }
}
