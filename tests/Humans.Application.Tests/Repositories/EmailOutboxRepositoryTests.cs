using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Email;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

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
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // AddAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddAsync_PersistsRow()
    {
        var msg = BuildMessage();
        await _repo.AddAsync(msg);

        var persisted = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync();
        persisted.Id.Should().Be(msg.Id);
        persisted.RecipientEmail.Should().Be(msg.RecipientEmail);
    }

    // ==========================================================================
    // Stats-shape reads
    // ==========================================================================

    [HumansFact]
    public async Task GetTotalCountAsync_CountsAllRows()
    {
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: _clock.GetCurrentInstant()));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Failed));

        var total = await _repo.GetTotalCountAsync();
        total.Should().Be(3);
    }

    [HumansFact]
    public async Task GetCountByStatusAsync_CountsOnlyMatchingStatus()
    {
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: _clock.GetCurrentInstant()));

        var queued = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Queued);
        var sent = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Sent);

        queued.Should().Be(2);
        sent.Should().Be(1);
    }

    [HumansFact]
    public async Task GetSentCountSinceAsync_OnlyCountsSentAfterCutoff()
    {
        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromHours(24);

        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromHours(48)));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromHours(6)));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: now - Duration.FromMinutes(5)));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued));

        var recent = await _repo.GetSentCountSinceAsync(cutoff);
        recent.Should().Be(2);
    }

    [HumansFact]
    public async Task GetRecentAsync_ReturnsNewestFirstAndRespectsLimit()
    {
        // Older to newer
        var msg1 = BuildMessage(createdAt: _clock.GetCurrentInstant() - Duration.FromMinutes(30));
        var msg2 = BuildMessage(createdAt: _clock.GetCurrentInstant() - Duration.FromMinutes(20));
        var msg3 = BuildMessage(createdAt: _clock.GetCurrentInstant() - Duration.FromMinutes(10));

        await _repo.AddAsync(msg1);
        await _repo.AddAsync(msg2);
        await _repo.AddAsync(msg3);

        var recent = await _repo.GetRecentAsync(take: 2);
        recent.Select(m => m.Id).Should().Equal(msg3.Id, msg2.Id);
    }

    [HumansFact]
    public async Task GetForUserAsync_ReturnsUserMessagesInDescendingCreatedAtOrder()
    {
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var older = BuildMessage(userId: userId, createdAt: _clock.GetCurrentInstant() - Duration.FromHours(2));
        var newer = BuildMessage(userId: userId, createdAt: _clock.GetCurrentInstant() - Duration.FromHours(1));
        var other = BuildMessage(userId: otherId);

        await _repo.AddAsync(older);
        await _repo.AddAsync(newer);
        await _repo.AddAsync(other);

        var forUser = await _repo.GetForUserAsync(userId);
        forUser.Select(m => m.Id).Should().Equal(newer.Id, older.Id);
    }

    [HumansFact]
    public async Task GetCountForUserAsync_OnlyCountsUsersRows()
    {
        var userId = Guid.NewGuid();
        await _repo.AddAsync(BuildMessage(userId: userId));
        await _repo.AddAsync(BuildMessage(userId: userId));
        await _repo.AddAsync(BuildMessage(userId: Guid.NewGuid()));

        (await _repo.GetCountForUserAsync(userId)).Should().Be(2);
    }

    [HumansFact]
    public async Task GetPendingCountAsync_OnlyCountsUnsentBelowRetryCap()
    {
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Queued));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 3));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Failed, retryCount: 10));
        await _repo.AddAsync(BuildMessage(status: EmailOutboxStatus.Sent, sentAt: _clock.GetCurrentInstant()));

        var pending = await _repo.GetPendingCountAsync(maxRetries: 10);
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
        await _repo.AddAsync(msg);

        var recipient = await _repo.RetryAsync(msg.Id);
        recipient.Should().Be(msg.RecipientEmail);

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync();
        reloaded.Status.Should().Be(EmailOutboxStatus.Queued);
        reloaded.RetryCount.Should().Be(0);
        reloaded.LastError.Should().BeNull();
        reloaded.NextRetryAt.Should().BeNull();
        reloaded.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task RetryAsync_ReturnsNullWhenMessageMissing()
    {
        var result = await _repo.RetryAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task DiscardAsync_DeletesAndReturnsRecipient()
    {
        var msg = BuildMessage();
        await _repo.AddAsync(msg);

        var recipient = await _repo.DiscardAsync(msg.Id);
        recipient.Should().Be(msg.RecipientEmail);

        (await _dbContext.EmailOutboxMessages.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task DiscardAsync_ReturnsNullWhenMessageMissing()
    {
        var result = await _repo.DiscardAsync(Guid.NewGuid());
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

        await _repo.AddAsync(eligible);
        await _repo.AddAsync(sentAlready);
        await _repo.AddAsync(atRetryCap);
        await _repo.AddAsync(futureRetry);
        await _repo.AddAsync(recentlyPickedUp);
        await _repo.AddAsync(stalePickedUp);

        var batch = await _repo.GetProcessingBatchAsync(now, stale, maxRetries: 10, batchSize: 100);
        batch.Select(m => m.Id).Should().BeEquivalentTo(new[] { eligible.Id, stalePickedUp.Id });
    }

    [HumansFact]
    public async Task MarkPickedUpAsync_SetsPickedUpAtForAllIds()
    {
        var m1 = BuildMessage();
        var m2 = BuildMessage();
        var untouched = BuildMessage();
        await _repo.AddAsync(m1);
        await _repo.AddAsync(m2);
        await _repo.AddAsync(untouched);

        var pickedUpAt = Instant.FromUtc(2026, 4, 1, 15, 0);
        await _repo.MarkPickedUpAsync(new[] { m1.Id, m2.Id }, pickedUpAt);

        var all = await _dbContext.EmailOutboxMessages.AsNoTracking().ToListAsync();
        all.Single(x => x.Id == m1.Id).PickedUpAt.Should().Be(pickedUpAt);
        all.Single(x => x.Id == m2.Id).PickedUpAt.Should().Be(pickedUpAt);
        all.Single(x => x.Id == untouched.Id).PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkSentAsync_FlipsStatusAndClearsPickedUpAt()
    {
        var msg = BuildMessage(status: EmailOutboxStatus.Queued);
        msg.PickedUpAt = _clock.GetCurrentInstant();
        await _repo.AddAsync(msg);

        var sentAt = Instant.FromUtc(2026, 4, 1, 15, 30);
        var result = await _repo.MarkSentAsync(msg.Id, sentAt);
        result.Should().BeTrue();

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync();
        reloaded.Status.Should().Be(EmailOutboxStatus.Sent);
        reloaded.SentAt.Should().Be(sentAt);
        reloaded.PickedUpAt.Should().BeNull();
    }

    [HumansFact]
    public async Task MarkSentAsync_ReturnsFalseWhenMessageMissing()
    {
        var result = await _repo.MarkSentAsync(Guid.NewGuid(), Instant.FromUtc(2026, 4, 1, 0, 0));
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task MarkFailedAsync_SetsFailureFieldsAndIncrementsRetry()
    {
        var msg = BuildMessage(status: EmailOutboxStatus.Queued, retryCount: 1);
        msg.PickedUpAt = _clock.GetCurrentInstant();
        await _repo.AddAsync(msg);

        var failedAt = _clock.GetCurrentInstant();
        var nextRetryAt = failedAt + Duration.FromMinutes(5);
        var result = await _repo.MarkFailedAsync(msg.Id, failedAt, "SMTP error", nextRetryAt);
        result.Should().BeTrue();

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync();
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
        await _repo.AddAsync(msg);

        var longMsg = new string('x', 5000);
        await _repo.MarkFailedAsync(msg.Id, _clock.GetCurrentInstant(), longMsg,
            _clock.GetCurrentInstant() + Duration.FromMinutes(1));

        var reloaded = await _dbContext.EmailOutboxMessages.AsNoTracking().SingleAsync();
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

        await _repo.AddAsync(oldSent);
        await _repo.AddAsync(recentSent);
        await _repo.AddAsync(oldFailed);
        await _repo.AddAsync(oldQueued);

        var deleted = await _repo.DeleteSentOlderThanAsync(cutoff);
        deleted.Should().Be(1);

        var remaining = await _dbContext.EmailOutboxMessages.AsNoTracking().Select(m => m.Id).ToListAsync();
        remaining.Should().BeEquivalentTo(new[] { recentSent.Id, oldFailed.Id, oldQueued.Id });
    }

    // ==========================================================================
    // Pause flag
    // ==========================================================================

    [HumansFact]
    public async Task GetSendingPausedAsync_ReturnsFalseWhenRowAbsent()
    {
        var paused = await _repo.GetSendingPausedAsync();
        paused.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetSendingPausedAsync_InsertsRowWhenAbsent()
    {
        await _repo.SetSendingPausedAsync(true);

        var paused = await _repo.GetSendingPausedAsync();
        paused.Should().BeTrue();

        var row = await _dbContext.SystemSettings.AsNoTracking()
            .SingleAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused);
        row.Value.Should().Be("true");
    }

    [HumansFact]
    public async Task SetSendingPausedAsync_UpdatesExistingRow()
    {
        _dbContext.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.IsEmailSendingPaused, Value = "true" });
        await _dbContext.SaveChangesAsync();

        await _repo.SetSendingPausedAsync(false);

        (await _repo.GetSendingPausedAsync()).Should().BeFalse();

        var count = await _dbContext.SystemSettings
            .CountAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused);
        count.Should().Be(1);
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
