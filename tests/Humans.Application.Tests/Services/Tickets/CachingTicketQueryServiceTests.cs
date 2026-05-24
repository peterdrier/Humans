using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class CachingTicketQueryServiceTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid UserC = Guid.NewGuid();

    private readonly ITicketService _inner;
    private readonly MemoryCache _memoryCache;
    private readonly FakeClock _clock;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _inner = Substitute.For<ITicketService>();

        var services = new ServiceCollection();
        services.AddKeyedScoped<ITicketService>(
            CachingTicketQueryService.InnerServiceKey,
            (_, _) => _inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _clock = new FakeClock(Instant.FromUtc(2026, 5, 1, 0, 0));
        _memoryCache = new MemoryCache(new MemoryCacheOptions());

        _decorator = new CachingTicketQueryService(
            _memoryCache,
            scopeFactory,
            _clock,
            NullLogger<CachingTicketQueryService>.Instance);

        SeedOrders();
        _inner.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserTicketHoldings(0, [])));
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_ProjectionDerivesCurrentTicketHolders()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
            MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.CheckedIn),
            MakeAttendee(matchedUserId: UserC, status: TicketAttendeeStatus.Void),
        ]));

        var result = (await _decorator.GetTicketOrdersAsync())
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .Where(a => a.MatchedUserId.HasValue
                && a.Status is TicketAttendeeStatus.Valid or TicketAttendeeStatus.CheckedIn)
            .Select(a => a.MatchedUserId!.Value)
            .ToHashSet();

        result.Should().BeEquivalentTo([UserA, UserB]);
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_CachesInnerProjection()
    {
        SeedOrders(
            MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []),
            MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
                MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
            ]));

        var first = await _decorator.GetTicketOrdersAsync();
        var second = await _decorator.GetTicketOrdersAsync();

        first.Should().BeEquivalentTo(second);
        await _inner.Received(1).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserTicketHoldingsAsync_UsesTrackedUserCache()
    {
        SeedHoldings(UserA, new UserTicketHoldings(1, [], TicketCount: 1));

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA);
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA);

        first.TicketCount.Should().Be(1);
        second.TicketCount.Should().Be(1);
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserTicketHoldingsAsync_ReloadsExpiredTrackedEntry()
    {
        var stale = new UserTicketHoldings(1, [], TicketCount: 1);
        var fresh = new UserTicketHoldings(1, [], TicketCount: 2);
        _inner.GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(stale), Task.FromResult(fresh));

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _clock.Advance(Duration.FromMinutes(6));
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA);

        first.TicketCount.Should().Be(1);
        second.TicketCount.Should().Be(2);
        await _inner.Received(2).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_DropsProjectionAndBothUsersPerUserEntries()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: UserB);

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _ = await _decorator.GetUserTicketHoldingsAsync(UserB);

        await _inner.Received(1).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserB, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_NullReceiver_LeavesOtherUsersAlone()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: null);

        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _ = await _decorator.GetUserTicketHoldingsAsync(UserB);

        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
        await _inner.DidNotReceive().GetUserTicketHoldingsAsync(UserB, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAfterUserMerge_DropsProjectionAndBothUsersPerUserEntries()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterUserMerge(sourceUserId: UserA, targetUserId: UserB);

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _ = await _decorator.GetUserTicketHoldingsAsync(UserB);

        await _inner.Received(1).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserB, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAll_DropsProjectionAndPerUserEntries()
    {
        await SeedOneUserHolding();

        _decorator.InvalidateAll();

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);

        await _inner.Received(1).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAfterContactImport_DropsProjectionAndAllUserHoldings()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterContactImport();

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _ = await _decorator.GetUserTicketHoldingsAsync(UserB);

        await _inner.Received(1).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserB, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void InvalidateVendorEventSummary_RemovesMemoryCacheEntry()
    {
        const string eventId = "ev_test";
        var key = CacheKeys.TicketEventSummary(eventId);
        _memoryCache.Set(key, "cached-summary");

        _decorator.InvalidateVendorEventSummary(eventId);

        _memoryCache.TryGetValue(key, out _).Should().BeFalse();
    }

    private async Task SeedTwoUserHoldings()
    {
        SeedOrders(
            MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
                MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
            ]),
            MakeOrder(Guid.NewGuid(), matchedUserId: UserB, attendees: [
                MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
            ]));
        SeedHoldings(UserA, new UserTicketHoldings(1, [], TicketCount: 1));
        SeedHoldings(UserB, new UserTicketHoldings(1, [], TicketCount: 1));

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _ = await _decorator.GetUserTicketHoldingsAsync(UserB);
        _inner.ClearReceivedCalls();
    }

    private async Task SeedOneUserHolding()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
        ]));
        SeedHoldings(UserA, new UserTicketHoldings(1, [], TicketCount: 1));

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _inner.ClearReceivedCalls();
    }

    private void SeedOrders(params TicketOrderInfo[] orders)
    {
        _inner.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TicketOrderInfo>>(orders));
    }

    private void SeedHoldings(Guid userId, UserTicketHoldings holdings) =>
        _inner.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(holdings));

    private static TicketOrderInfo MakeOrder(
        Guid id, Guid? matchedUserId, params TicketAttendeeInfo[] attendees) => new(
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
            IsCurrentEvent: true,
            Attendees: attendees.ToList());

    private static TicketAttendeeInfo MakeAttendee(
        Guid? matchedUserId,
        TicketAttendeeStatus status,
        string? email = null) => new(
            Id: Guid.NewGuid(),
            VendorTicketId: $"tkt_{Guid.NewGuid():N}",
            AttendeeName: "Attendee",
            AttendeeEmail: email,
            TicketTypeName: "Full Week",
            Price: 50m,
            Status: status,
            MatchedUserId: matchedUserId);
}
