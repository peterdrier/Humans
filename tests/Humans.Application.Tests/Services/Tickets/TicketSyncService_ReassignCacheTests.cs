using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

/// <summary>
/// Pins the account-merge-fold cache-invalidation invariant on
/// <see cref="TicketSyncService.ReassignAsync"/> (T-07 spec, step A).
///
/// <para>
/// When the orchestrator (<c>AccountMergeService.AcceptAsync</c>) fans out to
/// every <c>IUserMerge</c>, the Tickets section re-FKs <c>TicketOrder.MatchedUserId</c>,
/// <c>TicketAttendee.MatchedUserId</c>, and the two <c>TicketTransferRequest</c>
/// user columns from source to target. Before T-07 the per-user cache entries
/// (<c>UserTicketCount:{userId}</c>, <c>UserTicketHoldings:{userId}</c>) were
/// NOT evicted on this path — the global <c>InvalidateTicketCaches</c> helper
/// skips per-user keys because they can't be enumerated for bulk invalidation.
/// That's the gap this test pins closed: after a merge, both source's and
/// target's per-user entries must be gone.
/// </para>
///
/// <para>
/// Uses NSubstitute for the two repositories (rather than the real EF-backed
/// pair) so we can drive <see cref="TicketSyncService.ReassignAsync"/> without
/// hitting <c>ExecuteUpdateAsync</c> — unsupported by the in-memory provider.
/// The test is targeting the invalidation contract, not the SQL.
/// </para>
/// </summary>
public sealed class TicketSyncService_ReassignCacheTests
{
    [HumansFact]
    public async Task ReassignAsync_DropsGlobalAndBothUsersPerUserCacheEntries()
    {
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var cache = new MemoryCache(new MemoryCacheOptions());

        // Global ticket caches — currently cleared by InvalidateTicketCaches
        cache.Set(CacheKeys.TicketDashboardStats, "stale-dashboard");
        cache.Set(CacheKeys.UserIdsWithTickets, new HashSet<Guid>());
        cache.Set(CacheKeys.ValidAttendeeEmails, new List<string>());

        // Per-user entries — the gap this test pins closed. Both users must be
        // evicted: source's tickets just moved to target, so source's count is
        // meaningless and target's count is stale.
        cache.Set(CacheKeys.UserTicketCount(sourceUserId), 3);
        cache.Set(CacheKeys.UserTicketCount(targetUserId), 1);
        cache.Set(
            CacheKeys.UserTicketHoldings(sourceUserId),
            new UserTicketHoldings(3, []));
        cache.Set(
            CacheKeys.UserTicketHoldings(targetUserId),
            new UserTicketHoldings(1, []));

        var service = new TicketSyncService(
            Substitute.For<ITicketRepository>(),
            Substitute.For<ITicketTransferRepository>(),
            Substitute.For<ITicketVendorService>(),
            Substitute.For<IStripeService>(),
            new FakeClock(Instant.FromUtc(2026, 5, 16, 12, 0)),
            Options.Create(new TicketVendorSettings { EventId = "ev_t07", ApiKey = "k", SyncIntervalMinutes = 15 }),
            NullLogger<TicketSyncService>.Instance,
            cache,
            Substitute.For<IUserService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IShiftManagementService>());

        await service.ReassignAsync(
            sourceUserId,
            targetUserId,
            actorUserId: Guid.NewGuid(),
            updatedAt: Instant.FromUtc(2026, 5, 16, 12, 0),
            CancellationToken.None);

        cache.TryGetValue(CacheKeys.TicketDashboardStats, out _).Should().BeFalse();
        cache.TryGetValue(CacheKeys.UserIdsWithTickets, out _).Should().BeFalse();
        cache.TryGetValue(CacheKeys.ValidAttendeeEmails, out _).Should().BeFalse();

        cache.TryGetValue(CacheKeys.UserTicketCount(sourceUserId), out _).Should().BeFalse(
            because: "source user's cached ticket count is meaningless after their tickets were re-FK'd to the target");
        cache.TryGetValue(CacheKeys.UserTicketCount(targetUserId), out _).Should().BeFalse(
            because: "target user's cached ticket count is stale — source's tickets just landed on it");
        cache.TryGetValue(CacheKeys.UserTicketHoldings(sourceUserId), out _).Should().BeFalse();
        cache.TryGetValue(CacheKeys.UserTicketHoldings(targetUserId), out _).Should().BeFalse();
    }
}
