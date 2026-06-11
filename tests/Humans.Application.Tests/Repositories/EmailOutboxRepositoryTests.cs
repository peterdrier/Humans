using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Email;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Repositories;

public sealed class EmailOutboxRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly EmailOutboxRepository _repo;

    public EmailOutboxRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 1, 12, 0));
        _repo = new EmailOutboxRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    // ==========================================================================
    // AddAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddAsync_PersistsRow()
    {
        var msg = BuildMessage();
        await _repo.AddAsync(msg, Xunit.TestContext.Current.CancellationToken);

        var persisted = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        persisted.Id.Should().Be(msg.Id);
        persisted.RecipientEmail.Should().Be(msg.RecipientEmail);
    }

    // ==========================================================================
    // Stats-shape reads
    // ==========================================================================

    [HumansFact]
    public async Task GetTotalCountAsync_CountsAllRows()
    {
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: _clock.GetCurrentInstant()), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Failed), Xunit.TestContext.Current.CancellationToken);

        var total = await _repo.GetTotalCountAsync(Xunit.TestContext.Current.CancellationToken);
        total.Should().Be(3);
    }

    [HumansFact]
    public async Task GetCountByStatusAsync_CountsOnlyMatchingStatus()
    {
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: _clock.GetCurrentInstant()), Xunit.TestContext.Current.CancellationToken);

        var queued = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Queued, Xunit.TestContext.Current.CancellationToken);
        var sent = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Sent, Xunit.TestContext.Current.CancellationToken);

        queued.Should().Be(2);
        sent.Should().Be(1);
    }

    [HumansFact]
    public async Task GetSentCountSinceAsync_OnlyCountsSentAfterCutoff()
    {
        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromHours(24);

        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromHours(48)), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromHours(6)), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromMinutes(5)), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued), Xunit.TestContext.Current.CancellationToken);

        var recent = await _repo.GetSentCountSinceAsync(cutoff, Xunit.TestContext.Current.CancellationToken);
        recent.Should().Be(2);
    }

    [HumansFact]
    public async Task GetRecentAsync_ReturnsNewestMessagesCapped()
    {
        // Older to newer
        var msg1 = BuildMessage(createdAt: _clock.GetCurrentInstant() - Duration.FromMinutes(30));
        var msg2 = BuildMessage(createdAt: _clock.GetCurrentInstant() - Duration.FromMinutes(20));
        var msg3 = BuildMessage(createdAt: _clock.GetCurrentInstant() - Duration.FromMinutes(10));

        await _repo.AddAsync(msg1, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(msg2, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(msg3, Xunit.TestContext.Current.CancellationToken);

        var recent = await _repo.GetRecentAsync(2, Xunit.TestContext.Current.CancellationToken);
        recent.Select(m => m.Id).Should().Equal(msg3.Id, msg2.Id);
    }

    [HumansFact]
    public async Task GetForUserAsync_ReturnsUserMessages()
    {
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var older = BuildMessage(userId: userId, createdAt: _clock.GetCurrentInstant() - Duration.FromHours(2));
        var newer = BuildMessage(userId: userId, createdAt: _clock.GetCurrentInstant() - Duration.FromHours(1));
        var other = BuildMessage(userId: otherId);

        await _repo.AddAsync(older, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(newer, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(other, Xunit.TestContext.Current.CancellationToken);

        var forUser = await _repo.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        forUser.Select(m => m.Id).Should().BeEquivalentTo([newer.Id, older.Id]);
    }

    [HumansFact]
    public async Task GetCountForUserAsync_OnlyCountsUsersRows()
    {
        var userId = Guid.NewGuid();
        await _repo.AddAsync(BuildMessage(userId: userId), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(userId: userId), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(userId: Guid.NewGuid()), Xunit.TestContext.Current.CancellationToken);

        (await _repo.GetCountForUserAsync(userId, Xunit.TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [HumansFact]
    public async Task GetPendingCountAsync_OnlyCountsUnsentBelowRetryCap()
    {
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 3), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 10), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: _clock.GetCurrentInstant()), Xunit.TestContext.Current.CancellationToken);

        var pending = await _repo.GetPendingCountAsync(maxRetries: 10, ct: Xunit.TestContext.Current.CancellationToken);
        // Queued (0) + Failed with 3 retries (below 10) = 2; Failed with 10 retries is at cap; Sent excluded.
        pending.Should().Be(2);
    }

    // ==========================================================================
    // Retry / Discard
    // ==========================================================================

    [HumansFact]
    public async Task RetryAsync_ResetsStatusAndCountersAndReturnsRecipient()
    {
        var msg = BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 3);
        msg.LastError = "boom";
        msg.NextRetryAt = _clock.GetCurrentInstant() + Duration.FromMinutes(10);
        msg.PickedUpAt = _clock.GetCurrentInstant();
        await _repo.AddAsync(msg, Xunit.TestContext.Current.CancellationToken);

        var recipient = await _repo.RetryAsync(msg.Id, Xunit.TestContext.Current.CancellationToken);
        recipient.Should().Be(msg.RecipientEmail);

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(EmailOutboxStatus.Queued);
        reloaded.RetryCount.Should().Be(0);
        reloaded.LastError.Should().BeNull();
        reloaded.NextRetryAt.Should().BeNull();
        reloaded.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task RetryAsync_ReturnsNullWhenMessageMissing()
    {
        var result = await _repo.RetryAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task DiscardAsync_DeletesAndReturnsRecipient()
    {
        var msg = BuildMessage();
        await _repo.AddAsync(msg, Xunit.TestContext.Current.CancellationToken);

        var recipient = await _repo.DiscardAsync(msg.Id, Xunit.TestContext.Current.CancellationToken);
        recipient.Should().Be(msg.RecipientEmail);

        (await _dbContext.EmailOutboxMessages.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task DiscardAsync_ReturnsNullWhenMessageMissing()
    {
        var result = await _repo.DiscardAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    // ==========================================================================
    // Processor helpers
    // ==========================================================================

    [HumansFact]
    public async Task GetProcessingBatchAsync_FiltersOnAllCriteria()
    {
        var now = _clock.GetCurrentInstant();
        var stale = now - Duration.FromMinutes(5);

        var eligible = BuildMessage(status: EmailOutboxStatus.Queued);
        var sentAlready = BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now);
        var atRetryCap = BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 10);
        var futureRetry = BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 2);
        futureRetry.NextRetryAt = now + Duration.FromMinutes(5);
        var recentlyPickedUp = BuildMessage(status: EmailOutboxStatus.Queued);
        recentlyPickedUp.PickedUpAt = now - Duration.FromMinutes(2);
        var stalePickedUp = BuildMessage(status: EmailOutboxStatus.Queued);
        stalePickedUp.PickedUpAt = now - Duration.FromMinutes(10);

        await _repo.AddAsync(eligible, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(sentAlready, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(atRetryCap, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(futureRetry, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(recentlyPickedUp, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(stalePickedUp, Xunit.TestContext.Current.CancellationToken);

        var batch = await _repo.GetProcessingBatchAsync(now, stale, maxRetries: 10, batchSize: 100, ct: Xunit.TestContext.Current.CancellationToken);
        batch.Select(m => m.Id).Should().BeEquivalentTo([eligible.Id, stalePickedUp.Id]);
    }

    [HumansFact]
    public async Task MarkPickedUpAsync_SetsPickedUpAtForAllIds()
    {
        var m1 = BuildMessage();
        var m2 = BuildMessage();
        var untouched = BuildMessage();
        await _repo.AddAsync(m1, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(m2, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(untouched, Xunit.TestContext.Current.CancellationToken);

        var pickedUpAt = Instant.FromUtc(2026, 4, 1, 15, 0);
        await _repo.MarkPickedUpAsync([m1.Id, m2.Id], pickedUpAt, Xunit.TestContext.Current.CancellationToken);

        var all = await _dbContext.EmailOutboxMessages.AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        all.Single(x => x.Id == m1.Id).PickedUpAt.Should().Be(pickedUpAt);
        all.Single(x => x.Id == m2.Id).PickedUpAt.Should().Be(pickedUpAt);
        all.Single(x => x.Id == untouched.Id).PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkSentAsync_FlipsStatusAndClearsPickedUpAt()
    {
        var msg = BuildMessage(status: EmailOutboxStatus.Queued);
        msg.PickedUpAt = _clock.GetCurrentInstant();
        await _repo.AddAsync(msg, Xunit.TestContext.Current.CancellationToken);

        var sentAt = Instant.FromUtc(2026, 4, 1, 15, 30);
        var result = await _repo.MarkSentAsync(msg.Id, sentAt, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(EmailOutboxStatus.Sent);
        reloaded.SentAt.Should().Be(sentAt);
        reloaded.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkSentAsync_ReturnsFalseWhenMessageMissing()
    {
        var result = await _repo.MarkSentAsync(Guid.NewGuid(), Instant.FromUtc(2026, 4, 1, 0, 0), Xunit.TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task MarkFailedAsync_SetsFailureFieldsAndIncrementsRetry()
    {
        var msg = BuildMessage(status: EmailOutboxStatus.Queued, retryCount: 1);
        msg.PickedUpAt = _clock.GetCurrentInstant();
        await _repo.AddAsync(msg, Xunit.TestContext.Current.CancellationToken);

        var failedAt = _clock.GetCurrentInstant();
        var nextRetryAt = failedAt + Duration.FromMinutes(5);
        var result = await _repo.MarkFailedAsync(msg.Id, failedAt, "SMTP error", nextRetryAt, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(EmailOutboxStatus.Failed);
        reloaded.RetryCount.Should().Be(2);
        reloaded.LastError.Should().Be("SMTP error");
        reloaded.NextRetryAt.Should().Be(nextRetryAt);
        reloaded.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkFailedAsync_TruncatesLongErrors()
    {
        var msg = BuildMessage();
        await _repo.AddAsync(msg, Xunit.TestContext.Current.CancellationToken);

        var longMsg = new string('x', 5000);
        await _repo.MarkFailedAsync(msg.Id, _clock.GetCurrentInstant(), longMsg,
            _clock.GetCurrentInstant() + Duration.FromMinutes(1), Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        reloaded.LastError!.Length.Should().Be(4000);
    }

    // ==========================================================================
    // Cleanup
    // ==========================================================================

    [HumansFact]
    public async Task DeleteSentOlderThanAsync_RemovesOnlyOldSentMessages()
    {
        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(30);

        var oldSent = BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromDays(60));
        var recentSent = BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromDays(5));
        var oldFailed = BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 3);
        var oldQueued = BuildMessage(status: EmailOutboxStatus.Queued);

        await _repo.AddAsync(oldSent, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(recentSent, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(oldFailed, Xunit.TestContext.Current.CancellationToken);
        await _repo.AddAsync(oldQueued, Xunit.TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteSentOlderThanAsync(cutoff, Xunit.TestContext.Current.CancellationToken);
        deleted.Should().Be(1);

        var remaining = await _dbContext.EmailOutboxMessages.AsNoTracking().Select(m => m.Id).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().BeEquivalentTo([recentSent.Id, oldFailed.Id, oldQueued.Id]);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private EmailOutboxMessage BuildMessage(
        Guid? userId = null,
        EmailOutboxStatus status = EmailOutboxStatus.Queued,
        int retryCount = 0,
        Instant? sentAt = null,
        Instant? createdAt = null) => new()
        {
            Id = Guid.NewGuid(),
            RecipientEmail = $"user-{Guid.NewGuid():N}@example.com",
            RecipientName = "Recipient",
            Subject = "Hello",
            HtmlBody = "<p>Hi</p>",
            PlainTextBody = "Hi",
            TemplateName = "test",
            UserId = userId,
            Status = status,
            RetryCount = retryCount,
            SentAt = sentAt,
            CreatedAt = createdAt ?? _clock.GetCurrentInstant(),
        };
}
