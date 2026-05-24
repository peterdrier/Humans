using AwesomeAssertions;
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
/// previously-cached <see cref="ITicketQueryService"/> reads now answer
/// from that in-memory shape. Per-user <c>UserTicketCount</c> and
/// <c>UserTicketHoldings</c> entries stay as separate short-TTL
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

    private readonly ITicketQueryService _inner;
    private readonly MemoryCache _perUserCache;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _inner = Substitute.For<ITicketQueryService>();
        _perUserCache = new MemoryCache(new MemoryCacheOptions());

        var services = new ServiceCollection();
        services.AddSingleton(_perUserCache);
        services.AddKeyedScoped<ITicketQueryService>(
            CachingTicketQueryService.InnerServiceKey,
            (_, _) => _inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _decorator = new CachingTicketQueryService(
            _perUserCache,
            scopeFactory,
            NullLogger<CachingTicketQueryService>.Instance);

        _inner.GetOrderInfosForCacheAsync(Arg.Any<CancellationToken>())
            .Returns([]);
    }

    [HumansFact]
    public async Task GetAllMatchedUserIdsAsync_UnionsBuyerAndAttendeeMatches()
    {
        SeedOrders(
            MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []),
            MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
                MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
            ]));

        var result = await _decorator.GetAllMatchedUserIdsAsync();

        result.Should().BeEquivalentTo([UserA, UserB]);
    }

    [HumansFact]
    public async Task HasTicketAttendeeMatchAsync_TrueForBuyerOrAttendeeMatch()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
            MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
        ]));

        (await _decorator.HasTicketAttendeeMatchAsync(UserA)).Should().BeTrue();
        (await _decorator.HasTicketAttendeeMatchAsync(UserB)).Should().BeTrue();
        (await _decorator.HasTicketAttendeeMatchAsync(UserC)).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetUserTicketCountAsync_CachesResult_AcrossCalls()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.CheckedIn),
        ]));

        var first = await _decorator.GetUserTicketCountAsync(UserA);
        var second = await _decorator.GetUserTicketCountAsync(UserA);

        first.Should().Be(2);
        second.Should().Be(2);
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeTrue(
            because: "the per-user TTL entry is preserved as a separate concern from the main projection");
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_DropsProjectionAndBothUsersPerUserEntries()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 5);
        _perUserCache.Set(CacheKeys.UserTicketCount(UserB), 0);
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA),
            new UserTicketHoldings(1, []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB),
            new UserTicketHoldings(0, []));

        // Force initial projection warm before invalidation.
        _ = await _decorator.GetAllMatchedUserIdsAsync();
        _decorator.Entries.Should().BeGreaterThan(0);

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: UserB);

        _decorator.Entries.Should().Be(0, because: "projection should be dropped");
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserB), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_NullReceiver_LeavesOtherUsersAlone()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 5);
        _perUserCache.Set(CacheKeys.UserTicketCount(UserB), 7);

        _ = await _decorator.GetAllMatchedUserIdsAsync();

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: null);

        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserB), out _).Should().BeTrue(
            because: "only the sender side of a half-failed transfer changes; unrelated users keep their entries");
    }

    [HumansFact]
    public async Task InvalidateAfterUserMerge_DropsProjectionAndBothUsersPerUserEntries()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 3);
        _perUserCache.Set(CacheKeys.UserTicketCount(UserB), 1);
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA),
            new UserTicketHoldings(3, []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB),
            new UserTicketHoldings(1, []));

        _ = await _decorator.GetAllMatchedUserIdsAsync();
        _decorator.Entries.Should().BeGreaterThan(0);

        _decorator.InvalidateAfterUserMerge(sourceUserId: UserA, targetUserId: UserB);

        _decorator.Entries.Should().Be(0);
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeFalse(
            because: "source user's count is meaningless after their tickets re-FK'd onto target");
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserB), out _).Should().BeFalse(
            because: "target user's count is stale — source's tickets just landed on it");
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task InvalidateAll_DropsProjection_LeavesPerUserEntriesAlone()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []));
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 3);

        _ = await _decorator.GetAllMatchedUserIdsAsync();

        _decorator.InvalidateAll();

        _decorator.Entries.Should().Be(0);
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeTrue(
            because: "InvalidateAll (used by the sync job) lets per-user entries expire via TTL — same policy as the contact-import path");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SeedOrders(params TicketOrder[] orders)
    {
        _inner.GetOrderInfosForCacheAsync(Arg.Any<CancellationToken>())
            .Returns(orders.Select(ProjectOrderInfo).ToList());
    }

    private static TicketOrderInfo ProjectOrderInfo(TicketOrder order) => new(
        order.Id,
        order.VendorOrderId,
        order.BuyerName,
        order.BuyerEmail,
        order.TotalAmount,
        order.Currency,
        order.DiscountCode,
        order.PaymentStatus,
        order.VendorEventId,
        order.PurchasedAt,
        order.MatchedUserId,
        order.Attendees.Select(ProjectAttendeeInfo).ToList());

    private static TicketAttendeeInfo ProjectAttendeeInfo(TicketAttendee attendee) => new(
        attendee.Id,
        attendee.VendorTicketId,
        attendee.AttendeeName,
        attendee.AttendeeEmail,
        attendee.TicketTypeName,
        attendee.Price,
        attendee.Status,
        attendee.MatchedUserId);

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
