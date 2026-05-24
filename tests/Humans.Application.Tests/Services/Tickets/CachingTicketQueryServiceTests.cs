using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class CachingTicketQueryServiceTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private readonly ITicketQueryService _inner;
    private readonly MemoryCache _perUserCache;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _inner = Substitute.For<ITicketQueryService>();
        _perUserCache = new MemoryCache(new MemoryCacheOptions());

        var services = new ServiceCollection();
        services.AddKeyedScoped<ITicketQueryService>(
            CachingTicketQueryService.InnerServiceKey,
            (_, _) => _inner);

        _decorator = new CachingTicketQueryService(
            _perUserCache,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTicketQueryService>.Instance);

        _inner.GetTicketOrderInfosAsync(Arg.Any<CancellationToken>())
            .Returns([]);
    }

    [HumansFact]
    public async Task GetUserTicketCountAsync_UsesOrderProjectionBeforeInnerFallback()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), UserB, [
            MakeAttendee(UserA, TicketAttendeeStatus.Valid),
            MakeAttendee(UserA, TicketAttendeeStatus.CheckedIn),
        ]));

        var first = await _decorator.GetUserTicketCountAsync(UserA);
        var second = await _decorator.GetUserTicketCountAsync(UserA);

        first.Should().Be(2);
        second.Should().Be(2);
        await _inner.DidNotReceive().GetUserTicketCountAsync(UserA);
    }

    [HumansFact]
    public async Task GetUserTicketHoldingsAsync_UsesOrderProjectionAndCachesResult()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), UserA, [
            MakeAttendee(null, TicketAttendeeStatus.Valid, name: "Buyer visible"),
            MakeAttendee(UserB, TicketAttendeeStatus.Valid, name: "Other user"),
        ]));

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA);
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA);

        first.OrderCount.Should().Be(1);
        first.Tickets.Should().ContainSingle(t => t.AttendeeName == "Buyer visible");
        second.Should().BeEquivalentTo(first);
        await _inner.DidNotReceive().GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_DropsProjectionAndAffectedPerUserEntries()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), UserA, []));
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 5);
        _perUserCache.Set(CacheKeys.UserTicketCount(UserB), 0);
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA), new UserTicketHoldings(1, []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB), new UserTicketHoldings(0, []));

        _ = await _decorator.GetAllMatchedUserIdsAsync();
        _decorator.Entries.Should().Be(1);

        _decorator.InvalidateAfterTransfer(UserA, UserB);

        _decorator.Entries.Should().Be(0);
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserB), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task InvalidateAll_DropsProjection_LeavesPerUserEntries()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), UserA, []));
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 3);

        _ = await _decorator.GetAllMatchedUserIdsAsync();
        _decorator.Entries.Should().Be(1);

        _decorator.InvalidateAll();

        _decorator.Entries.Should().Be(0);
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeTrue();
    }

    [HumansFact]
    public async Task PassThroughRead_DelegatesToInner()
    {
        var expected = Instant.FromUtc(2026, 5, 1, 0, 0);
        _inner.GetPostEventHoldDateAsync(Arg.Any<CancellationToken>())
            .Returns(expected);

        var actual = await _decorator.GetPostEventHoldDateAsync();

        actual.Should().Be(expected);
        await _inner.Received(1).GetPostEventHoldDateAsync(Arg.Any<CancellationToken>());
    }

    private void SeedOrders(params TicketOrderInfo[] orders)
    {
        _inner.GetTicketOrderInfosAsync(Arg.Any<CancellationToken>())
            .Returns(orders.ToList());
    }

    private static TicketOrderInfo MakeOrder(
        Guid id,
        Guid? matchedUserId,
        IReadOnlyList<TicketAttendeeInfo> attendees) => new(
            Id: id,
            VendorOrderId: $"ord_{id:N}",
            BuyerName: "Buyer",
            BuyerEmail: "buyer@example.com",
            TotalAmount: 100m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "ev_test",
            PurchasedAt: Instant.FromUtc(2026, 5, 1, 0, 0),
            MatchedUserId: matchedUserId,
            Attendees: attendees);

    private static TicketAttendeeInfo MakeAttendee(
        Guid? matchedUserId,
        TicketAttendeeStatus status,
        string name = "Attendee") => new(
            Id: Guid.NewGuid(),
            VendorTicketId: $"tkt_{Guid.NewGuid():N}",
            AttendeeName: name,
            AttendeeEmail: null,
            TicketTypeName: "Full Week",
            Price: 50m,
            Status: status,
            MatchedUserId: matchedUserId);
}
