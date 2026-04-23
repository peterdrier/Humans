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

    private void Seed(
        Instant? processedAt = null,
        int retryCount = 0,
        string? lastError = null,
        bool failedPermanently = false)
    {
        _seedContext.GoogleSyncOutboxEvents.Add(new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = "test",
            TeamId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            OccurredAt = Instant.FromUtc(2026, 4, 22, 10, 0),
            ProcessedAt = processedAt,
            RetryCount = retryCount,
            LastError = lastError,
            FailedPermanently = failedPermanently,
            DeduplicationKey = Guid.NewGuid().ToString(),
        });
        _seedContext.SaveChanges();
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
