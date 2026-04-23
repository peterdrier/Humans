using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Behavior tests for <see cref="GoogleSyncOutboxRepository"/> — the narrow
/// count surface introduced for Part 1 of issue #554 so Notifications,
/// Metrics, and the Admin daily digest consumers stop reading
/// <c>google_sync_outbox_events</c> directly.
///
/// <para>
/// Each count method is exercised against a fixture that intentionally
/// contains every kind of outbox row — pending, processed, pending-with-
/// error, permanently failed, retried — so a future tweak that
/// accidentally reclassifies a row is caught here rather than in
/// production admin dashboards.
/// </para>
/// </summary>
public sealed class GoogleSyncOutboxRepositoryTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly IGoogleSyncOutboxRepository _repository;
    private readonly HumansDbContext _seedContext;

    public GoogleSyncOutboxRepositoryTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repository = new GoogleSyncOutboxRepository(new SingleContextFactory(_options));
        _seedContext = new HumansDbContext(_options);
    }

    public void Dispose()
    {
        _seedContext.Dispose();
    }

    [Fact]
    public async Task CountPendingAsync_CountsOnlyUnprocessed()
    {
        Seed(processedAt: null); // pending
        Seed(processedAt: null, lastError: "err"); // pending-with-error
        Seed(processedAt: null, failedPermanently: true, lastError: "perm"); // permanent-in-flight
        Seed(processedAt: Instant.FromUtc(2026, 4, 23, 12, 0)); // processed — excluded

        var count = await _repository.CountPendingAsync();

        count.Should().Be(3);
    }

    [Fact]
    public async Task CountFailedAsync_UnprocessedWithError_IncludesPermanent()
    {
        Seed(processedAt: null); // pending, no error
        Seed(processedAt: null, lastError: "transient"); // matches
        Seed(processedAt: null, failedPermanently: true, lastError: "perm"); // also matches (not processed yet)
        Seed(processedAt: Instant.FromUtc(2026, 4, 23, 12, 0), lastError: "old"); // processed — excluded

        var count = await _repository.CountFailedAsync();

        count.Should().Be(2);
    }

    [Fact]
    public async Task CountStaleAsync_ExcludesPermanentFailures()
    {
        Seed(processedAt: null, lastError: "transient-1");
        Seed(processedAt: null, lastError: "transient-2");
        Seed(processedAt: null, failedPermanently: true, lastError: "perm"); // excluded
        Seed(processedAt: null); // no error — excluded

        var count = await _repository.CountStaleAsync();

        count.Should().Be(2);
    }

    [Fact]
    public async Task CountTransientRetriesAsync_RequiresRetryCountAboveZero()
    {
        Seed(processedAt: null, retryCount: 0, lastError: "first-attempt"); // excluded (never retried)
        Seed(processedAt: null, retryCount: 2, lastError: "retrying"); // matches
        Seed(processedAt: null, retryCount: 5, lastError: null); // matches (LastError can be null)
        Seed(processedAt: null, retryCount: 3, lastError: "perm", failedPermanently: true); // excluded
        Seed(processedAt: Instant.FromUtc(2026, 4, 23, 12, 0), retryCount: 4); // processed — excluded

        var count = await _repository.CountTransientRetriesAsync();

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetRecentAsync_OrdersByOccurredAtDescending_AndAppliesTakeLimit()
    {
        var oldest = Seed(occurredAt: Instant.FromUtc(2026, 4, 20, 10, 0));
        var middle = Seed(occurredAt: Instant.FromUtc(2026, 4, 22, 10, 0));
        var newest = Seed(occurredAt: Instant.FromUtc(2026, 4, 23, 10, 0));

        var page = await _repository.GetRecentAsync(2);

        page.Should().HaveCount(2);
        page[0].Id.Should().Be(newest);
        page[1].Id.Should().Be(middle);
    }

    [Fact]
    public async Task GetProcessingBatchAsync_ExcludesProcessedPermanentAndExhaustedRows_OrdersByOldest()
    {
        var oldest = Seed(occurredAt: Instant.FromUtc(2026, 4, 20, 10, 0));
        var newest = Seed(occurredAt: Instant.FromUtc(2026, 4, 23, 10, 0));
        Seed(processedAt: Instant.FromUtc(2026, 4, 22, 10, 0)); // processed — excluded
        Seed(failedPermanently: true, lastError: "perm"); // permanent — excluded
        Seed(retryCount: 5, lastError: "exhausted"); // at maxRetryCount=5 — excluded (RetryCount < max)

        var batch = await _repository.GetProcessingBatchAsync(batchSize: 10, maxRetryCount: 5);

        batch.Select(e => e.Id).Should().Equal(oldest, newest);
    }

    [Fact]
    public async Task MarkProcessedAsync_StampsProcessedAtAndClearsLastError()
    {
        var id = Seed(lastError: "transient");
        var at = Instant.FromUtc(2026, 4, 23, 14, 0);

        await _repository.MarkProcessedAsync(id, at);

        var stored = await _seedContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.ProcessedAt.Should().Be(at);
        stored.LastError.Should().BeNull();
        stored.FailedPermanently.Should().BeFalse();
    }

    [Fact]
    public async Task MarkProcessedAsync_MissingRow_NoThrow()
    {
        var act = async () => await _repository.MarkProcessedAsync(Guid.NewGuid(), Instant.FromUtc(2026, 4, 23, 14, 0));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarkPermanentlyFailedAsync_SetsFlagProcessedAtAndTruncatesLastError()
    {
        var id = Seed();
        var at = Instant.FromUtc(2026, 4, 23, 14, 0);
        var longMessage = new string('x', 5000); // exceeds 4000 cap

        await _repository.MarkPermanentlyFailedAsync(id, at, longMessage);

        var stored = await _seedContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.FailedPermanently.Should().BeTrue();
        stored.ProcessedAt.Should().Be(at);
        stored.LastError.Should().HaveLength(4000);
    }

    [Fact]
    public async Task IncrementRetryAsync_BelowMax_IncrementsWithoutMarkingPermanent()
    {
        var id = Seed(retryCount: 1);
        var at = Instant.FromUtc(2026, 4, 23, 14, 0);

        var (exhausted, retryCount) = await _repository.IncrementRetryAsync(id, at, "flaky", maxRetryCount: 5);

        exhausted.Should().BeFalse();
        retryCount.Should().Be(2);
        var stored = await _seedContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.RetryCount.Should().Be(2);
        stored.LastError.Should().Be("flaky");
        stored.FailedPermanently.Should().BeFalse();
        stored.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task IncrementRetryAsync_AtMax_MarksPermanentAndStampsProcessedAt()
    {
        var id = Seed(retryCount: 4);
        var at = Instant.FromUtc(2026, 4, 23, 14, 0);

        var (exhausted, retryCount) = await _repository.IncrementRetryAsync(id, at, "final", maxRetryCount: 5);

        exhausted.Should().BeTrue();
        retryCount.Should().Be(5);
        var stored = await _seedContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.FailedPermanently.Should().BeTrue();
        stored.ProcessedAt.Should().Be(at);
        stored.LastError.Should().Be("final");
    }

    [Fact]
    public async Task IncrementRetryAsync_MissingRow_ReturnsFalseZero()
    {
        var (exhausted, retryCount) = await _repository.IncrementRetryAsync(
            Guid.NewGuid(), Instant.FromUtc(2026, 4, 23, 14, 0), "err", maxRetryCount: 5);

        exhausted.Should().BeFalse();
        retryCount.Should().Be(0);
    }

    private Guid Seed(
        Instant? processedAt = null,
        int retryCount = 0,
        string? lastError = null,
        bool failedPermanently = false,
        Instant? occurredAt = null)
    {
        var id = Guid.NewGuid();
        _seedContext.GoogleSyncOutboxEvents.Add(new GoogleSyncOutboxEvent
        {
            Id = id,
            EventType = "test",
            TeamId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            OccurredAt = occurredAt ?? Instant.FromUtc(2026, 4, 22, 10, 0),
            ProcessedAt = processedAt,
            RetryCount = retryCount,
            LastError = lastError,
            FailedPermanently = failedPermanently,
            DeduplicationKey = Guid.NewGuid().ToString(),
        });
        _seedContext.SaveChanges();
        return id;
    }

    private sealed class SingleContextFactory : IDbContextFactory<HumansDbContext>
    {
        private readonly DbContextOptions<HumansDbContext> _options;

        public SingleContextFactory(DbContextOptions<HumansDbContext> options) => _options = options;

        public HumansDbContext CreateDbContext() => new(_options);

        public Task<HumansDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new HumansDbContext(_options));
    }
}
