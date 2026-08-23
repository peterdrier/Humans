using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Email.Services;
using Humans.Base.Configuration;
using Humans.Settings.Contracts;

namespace Humans.Email.Tests.Services;

/// <summary>
/// The retention rule itself. It moved out of <c>CleanupEmailOutboxJob</c> and behind
/// <see cref="IEmailOutboxRetention"/> at the section's G5 move (design §15 step 6b) —
/// what is left of the job is a try/catch and a metric, which is why there is no job
/// test any more.
/// </summary>
public class EmailOutboxRetentionTests : IDisposable
{
    private readonly EmailDbContext _dbContext;
    private readonly IEmailOutboxRetention _retention;

    // "Now" is 2026-03-14. With 150-day retention, cutoff is 2025-10-15.
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public EmailOutboxRetentionTests()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new EmailDbContext(options);
        var settings = Options.Create(new EmailSettings { OutboxRetentionDays = 150 });
        var repo = new EmailOutboxRepository(new TestDbContextFactory<EmailDbContext>(options));

        _retention = new EmailOutboxService(
            repo, Substitute.For<ISettingsService>(), settings, new FakeClock(Now));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact(Timeout = 10000)]
    public async Task PurgeExpiredAsync_DeletesSentMessagesOlderThanRetentionPeriod()
    {
        // 200 days ago — older than the 150-day retention
        await SeedMessageAsync(EmailOutboxStatus.Sent, Now - Duration.FromDays(200));

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().Be(0);
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_KeepsSentMessagesWithinRetentionPeriod()
    {
        // 100 days ago — within the 150-day retention
        await SeedMessageAsync(EmailOutboxStatus.Sent, Now - Duration.FromDays(100));

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().Be(1);
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_KeepsFailedMessagesRegardlessOfAge()
    {
        // Failed message, 200 days old — should not be deleted
        await SeedMessageAsync(EmailOutboxStatus.Failed, Now - Duration.FromDays(200));

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().Be(1);
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_KeepsQueuedMessagesRegardlessOfAge()
    {
        // Queued message, 200 days old — should not be deleted
        await SeedMessageAsync(EmailOutboxStatus.Queued, Now - Duration.FromDays(200));

        await _retention.PurgeExpiredAsync(Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.EmailOutboxMessages.CountAsync(Xunit.TestContext.Current.CancellationToken);
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
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return message;
    }
}
