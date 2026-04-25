using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Xunit;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Metering;
using Humans.Infrastructure.Repositories.Email;
using Humans.Infrastructure.Services.Metering;

namespace Humans.Application.Tests.Jobs;

public class ProcessEmailOutboxJobTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly HumansDbContext _dbContext;
    private readonly IEmailTransport _transport;
    private readonly ICampaignService _campaignService;
    private readonly FakeClock _clock;
    private readonly MetersService _meters;
    private readonly IOptions<EmailSettings> _settings;
    private readonly EmailOutboxRepository _repo;
    private readonly ProcessEmailOutboxJob _job;

    public ProcessEmailOutboxJobTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(_options);
        _transport = Substitute.For<IEmailTransport>();
        _campaignService = Substitute.For<ICampaignService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 14, 12, 0));
        _meters = new MetersService(Substitute.For<ILogger<MetersService>>());
        _settings = Options.Create(new EmailSettings { OutboxBatchSize = 10, OutboxMaxRetries = 10 });
        var logger = Substitute.For<ILogger<ProcessEmailOutboxJob>>();
        _repo = new EmailOutboxRepository(new TestDbContextFactory(_options));

        _job = new ProcessEmailOutboxJob(_repo, _campaignService, _transport, _meters, _clock, _settings, logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _meters.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact(Timeout = 10000)]
    public async Task ExecuteAsync_ProcessesQueuedMessages()
    {
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);

        await _job.ExecuteAsync();

        await _transport.Received(1).SendAsync(
            message.RecipientEmail,
            message.RecipientName,
            message.Subject,
            message.HtmlBody,
            message.PlainTextBody,
            message.ReplyTo,
            Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        var updated = await FreshQuery().SingleAsync();
        updated.Status.Should().Be(EmailOutboxStatus.Sent);
        updated.SentAt.Should().Be(_clock.GetCurrentInstant());
        updated.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task ExecuteAsync_HandlesFailure()
    {
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);

        _transport.SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP timeout"));

        await _job.ExecuteAsync();

        var updated = await FreshQuery().SingleAsync();
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
    public async Task ExecuteAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 15; i++)
            await SeedMessageAsync(EmailOutboxStatus.Queued);

        var batchSettings = Options.Create(new EmailSettings { OutboxBatchSize = 10, OutboxMaxRetries = 10 });
        var job = new ProcessEmailOutboxJob(
            _repo, _campaignService, _transport, _meters, _clock, batchSettings,
            Substitute.For<ILogger<ProcessEmailOutboxJob>>());

        await job.ExecuteAsync();

        await _transport.Received(10).SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsPaused()
    {
        _dbContext.SystemSettings.Add(new SystemSetting { Key = "IsEmailSendingPaused", Value = "true" });
        await _dbContext.SaveChangesAsync();

        await SeedMessageAsync(EmailOutboxStatus.Queued);

        await _job.ExecuteAsync();

        await _transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact(Timeout = 10000)]
    public async Task ExecuteAsync_CrashRecovery()
    {
        // Message picked up 6 minutes ago but never completed (simulates crash)
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.PickedUpAt = _clock.GetCurrentInstant() - Duration.FromMinutes(6);
        await _dbContext.SaveChangesAsync();

        await _job.ExecuteAsync();

        await _transport.Received(1).SendAsync(
            message.RecipientEmail,
            Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        var updated = await FreshQuery().SingleAsync();
        updated.Status.Should().Be(EmailOutboxStatus.Sent);
        updated.SentAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsRecentlyPickedUp()
    {
        // Message picked up 2 minutes ago — still within the 5 minute window
        var message = await SeedMessageAsync(EmailOutboxStatus.Queued);
        message.PickedUpAt = _clock.GetCurrentInstant() - Duration.FromMinutes(2);
        await _dbContext.SaveChangesAsync();

        await _job.ExecuteAsync();

        await _transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact(Timeout = 10000)]
    public async Task ExecuteAsync_RetriesFailedWithBackoff()
    {
        // Failed message with RetryCount=3, NextRetryAt in the past
        var message = await SeedMessageAsync(EmailOutboxStatus.Failed);
        message.RetryCount = 3;
        message.NextRetryAt = _clock.GetCurrentInstant() - Duration.FromMinutes(1);
        message.LastError = "previous error";
        await _dbContext.SaveChangesAsync();

        await _job.ExecuteAsync();

        await _transport.Received(1).SendAsync(
            message.RecipientEmail,
            Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<IDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        var updated = await FreshQuery().SingleAsync();
        updated.Status.Should().Be(EmailOutboxStatus.Sent);
        updated.SentAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsFutureRetry()
    {
        // Failed message with NextRetryAt in the future — should not be processed
        var message = await SeedMessageAsync(EmailOutboxStatus.Failed);
        message.RetryCount = 2;
        message.NextRetryAt = _clock.GetCurrentInstant() + Duration.FromMinutes(10);
        await _dbContext.SaveChangesAsync();

        await _job.ExecuteAsync();

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
        var ctx = new HumansDbContext(_options);
        return ctx.EmailOutboxMessages.AsNoTracking();
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
        await _dbContext.SaveChangesAsync();
        return message;
    }
}
