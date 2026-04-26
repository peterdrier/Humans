using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Email;
using Humans.Infrastructure.Services.Metering;
using Xunit;

namespace Humans.Application.Tests.Jobs;

public class CleanupEmailOutboxJobTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly MetersService _meters;
    private readonly CleanupEmailOutboxJob _job;

    // "Now" is 2026-03-14. With 150-day retention, cutoff is 2025-10-15.
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public CleanupEmailOutboxJobTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Now);
        _meters = new MetersService(NullLogger<MetersService>.Instance);
        var logger = Substitute.For<ILogger<CleanupEmailOutboxJob>>();
        var settings = Options.Create(new EmailSettings { OutboxRetentionDays = 150 });
        var repo = new EmailOutboxRepository(new TestDbContextFactory(options));

        _job = new CleanupEmailOutboxJob(repo, _clock, settings, _meters, logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _meters.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact(Timeout = 10000)]
    public async Task ExecuteAsync_DeletesSentMessagesOlderThanRetentionPeriod()
    {
        // 200 days ago — older than the 150-day retention
        var old = await SeedMessageAsync(EmailOutboxStatus.Sent, Now - Duration.FromDays(200));

        await _job.ExecuteAsync();

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync();
        remaining.Should().Be(0);
    }

    [HumansFact]
    public async Task ExecuteAsync_KeepsSentMessagesWithinRetentionPeriod()
    {
        // 100 days ago — within the 150-day retention
        var recent = await SeedMessageAsync(EmailOutboxStatus.Sent, Now - Duration.FromDays(100));

        await _job.ExecuteAsync();

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync();
        remaining.Should().Be(1);
    }

    [HumansFact]
    public async Task ExecuteAsync_KeepsFailedMessagesRegardlessOfAge()
    {
        // Failed message, 200 days old — should not be deleted
        var old = await SeedMessageAsync(EmailOutboxStatus.Failed, Now - Duration.FromDays(200));

        await _job.ExecuteAsync();

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync();
        remaining.Should().Be(1);
    }

    [HumansFact]
    public async Task ExecuteAsync_KeepsQueuedMessagesRegardlessOfAge()
    {
        // Queued message, 200 days old — should not be deleted
        var old = await SeedMessageAsync(EmailOutboxStatus.Queued, Now - Duration.FromDays(200));

        await _job.ExecuteAsync();

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync();
        remaining.Should().Be(1);
    }

    private async Task<EmailOutboxMessage> SeedMessageAsync(EmailOutboxStatus status, Instant? sentAt)
    {
        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = "test@example.com",
            Subject = "Test",
            HtmlBody = "<p>Test</p>",
            TemplateName = "test",
            Status = status,
            CreatedAt = sentAt ?? Now,
            SentAt = sentAt
        };

        _dbContext.EmailOutboxMessages.Add(message);
        await _dbContext.SaveChangesAsync();
        return message;
    }
}
