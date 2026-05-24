using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

/// <summary>
/// Pins the projection-derived read paths and invalidation seams on
/// <see cref="CachingTicketQueryService"/> (T-07 decorator lift, step B).
///
/// <para>
/// The decorator owns the per-order <c>TicketOrderInfo</c> projection;
/// previously-cached <see cref="ITicketService"/> reads now answer
/// from that in-memory shape. Per-user <c>UserTicketHoldings</c> entries
/// stay as separate short-TTL
/// <see cref="IMemoryCache"/>-backed concerns and are evicted by the
/// transfer / merge invalidation seams.
/// </para>
///
/// <para>
/// Tests use a substituted <see cref="ITicketRepository"/> to seed the
/// projection. The pass-through methods (paged admin lists, exports,
/// dashboard, sales aggregates) are not exercised here — they delegate
/// to the inner via a keyed scope and have separate coverage on
/// <c>TicketQueryService</c>.
/// </para>
/// </summary>
public sealed class CachingTicketQueryServiceTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid UserC = Guid.NewGuid();

    private readonly ITicketRepository _repo;
    private readonly MemoryCache _perUserCache;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _repo = Substitute.For<ITicketRepository>();
        _perUserCache = new MemoryCache(new MemoryCacheOptions());

        // These tests do not drive the email-fallback holdings path, so the
        // keyed inner can stay unregistered.
        var services = new ServiceCollection();
        services.AddSingleton(_perUserCache);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _decorator = new CachingTicketQueryService(
            _repo,
            _perUserCache,
            scopeFactory,
            NullLogger<CachingTicketQueryService>.Instance);

        // Default: empty projection. Tests that need orders override via
        // SeedOrders(...) which re-stubs the repo and re-warms.
        _repo.GetAllOrdersWithAttendeesAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        // Default sync state: VendorEventId matches the "ev_test" id stamped on
        // MakeOrder/MakeAttendee, so event-scoped reads (GetUserIdsWithTicketsAsync)
        // see the seeded orders.
        _repo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "ev_test" });
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_ProjectionDerivesCurrentTicketHolders()
    {
        var orderId = Guid.NewGuid();
        SeedOrders(MakeOrder(orderId, matchedUserId: null, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
            MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.CheckedIn),
            MakeAttendee(matchedUserId: UserC, status: TicketAttendeeStatus.Void),
        ]));

        var result = (await _decorator.GetTicketOrdersAsync())
            .CurrentEventTicketHolderUserIds();

        result.Should().BeEquivalentTo([UserA, UserB],
            because: "void attendees don't count as ticket coverage; matched buyer-only orders also excluded");
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_ProjectionDerivesAllMatchedUserIds()
    {
        SeedOrders(
            MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []),
            MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
                MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
            ]));

        var result = (await _decorator.GetTicketOrdersAsync())
            .AllMatchedUserIds();

        result.Should().BeEquivalentTo([UserA, UserB]);
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_ProjectionDerivesBuyerOrAttendeeMatch()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
            MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
        ]));

        var matchedIds = (await _decorator.GetTicketOrdersAsync()).AllMatchedUserIds();

        matchedIds.Should().Contain(UserA);
        matchedIds.Should().Contain(UserB);
        matchedIds.Should().NotContain(UserC);
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_DropsProjectionAndBothUsersPerUserEntries()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA),
            new UserTicketHoldings(1, []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB),
            new UserTicketHoldings(0, []));

        // Force initial projection warm before invalidation.
        _ = await _decorator.GetTicketOrdersAsync();
        _decorator.Entries.Should().BeGreaterThan(0);

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: UserB);

        _decorator.Entries.Should().Be(0, because: "projection should be dropped");
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_NullReceiver_LeavesOtherUsersAlone()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA),
            new UserTicketHoldings(1, [], TicketCount: 5));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB),
            new UserTicketHoldings(0, [], TicketCount: 7));

        _ = await _decorator.GetTicketOrdersAsync();

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: null);

        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeTrue(
            because: "only the sender side of a half-failed transfer changes; unrelated users keep their entries");
    }

    [HumansFact]
    public async Task InvalidateAfterUserMerge_DropsProjectionAndBothUsersPerUserEntries()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA),
            new UserTicketHoldings(3, [], TicketCount: 3));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB),
            new UserTicketHoldings(1, [], TicketCount: 1));

        _ = await _decorator.GetTicketOrdersAsync();
        _decorator.Entries.Should().BeGreaterThan(0);

        _decorator.InvalidateAfterUserMerge(sourceUserId: UserA, targetUserId: UserB);

        _decorator.Entries.Should().Be(0);
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task InvalidateAll_DropsProjection_LeavesPerUserEntriesAlone()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA),
            new UserTicketHoldings(1, [], TicketCount: 3));

        _ = await _decorator.GetTicketOrdersAsync();

        _decorator.InvalidateAll();

        _decorator.Entries.Should().Be(0);
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeTrue(
            because: "InvalidateAll (used by the sync job) lets per-user entries expire via TTL — same policy as the contact-import path");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SeedOrders(params TicketOrder[] orders)
    {
        _repo.GetAllOrdersWithAttendeesAsync(Arg.Any<CancellationToken>())
            .Returns(orders);
    }

    private static TicketOrder MakeOrder(
        Guid id, Guid? matchedUserId, params TicketAttendee[] attendees) => new()
        {
            Id = id,
            VendorOrderId = $"ord_{id:N}",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            SyncedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            MatchedUserId = matchedUserId,
            Attendees = attendees.ToList(),
        };

    private static TicketAttendee MakeAttendee(
        Guid? matchedUserId,
        TicketAttendeeStatus status,
        string? email = null) => new()
        {
            Id = Guid.NewGuid(),
            VendorTicketId = $"tkt_{Guid.NewGuid():N}",
            AttendeeName = "Attendee",
            AttendeeEmail = email,
            TicketTypeName = "Full Week",
            Price = 50m,
            Status = status,
            VendorEventId = "ev_test",
            SyncedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            MatchedUserId = matchedUserId,
        };
}
