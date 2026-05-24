using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
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
    private static readonly Guid UserC = Guid.NewGuid();

    private readonly ITicketRepository _repo;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _repo = Substitute.For<ITicketRepository>();

        var services = new ServiceCollection();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _decorator = new CachingTicketQueryService(
            _repo,
            new MemoryCache(new MemoryCacheOptions()),
            scopeFactory,
            NullLogger<CachingTicketQueryService>.Instance);

        _repo.GetAllOrdersWithAttendeesAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "ev_test" });
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
    public async Task GetTicketOrdersAsync_ProjectionDerivesAllMatchedUserIds()
    {
        SeedOrders(
            MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: []),
            MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
                MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
            ]));

        var result = (await _decorator.GetTicketOrdersAsync())
            .SelectMany(o => o.MatchedUserId.HasValue
                ? o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value)
                    .Append(o.MatchedUserId.Value)
                : o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value))
            .ToHashSet();

        result.Should().BeEquivalentTo([UserA, UserB]);
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_ProjectionDerivesBuyerOrAttendeeMatch()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
            MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
        ]));

        var matchedIds = (await _decorator.GetTicketOrdersAsync())
            .SelectMany(o => o.MatchedUserId.HasValue
                ? o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value)
                    .Append(o.MatchedUserId.Value)
                : o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value))
            .ToHashSet();

        matchedIds.Should().Contain(UserA);
        matchedIds.Should().Contain(UserB);
        matchedIds.Should().NotContain(UserC);
    }

    [HumansFact]
    public async Task GetUserTicketHoldingsAsync_UsesTrackedUserCache()
    {
        UseNonCurrentEvent();
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
        ]));

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA);
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA);

        first.TicketCount.Should().Be(1);
        second.TicketCount.Should().Be(1);
        _decorator.UserHoldingsCacheStats.Entries.Should().Be(1);
        _decorator.UserHoldingsCacheStats.Hits.Should().Be(1);
        _decorator.UserHoldingsCacheStats.Misses.Should().Be(1);
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_DropsProjectionAndBothUsersPerUserEntries()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: UserB);

        _decorator.Entries.Should().Be(0);
        _decorator.UserHoldingsCacheStats.Entries.Should().Be(0);
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_NullReceiver_LeavesOtherUsersAlone()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterTransfer(senderUserId: UserA, receiverUserId: null);

        _decorator.UserHoldingsCacheStats.Entries.Should().Be(1);
    }

    [HumansFact]
    public async Task InvalidateAfterUserMerge_DropsProjectionAndBothUsersPerUserEntries()
    {
        await SeedTwoUserHoldings();

        _decorator.InvalidateAfterUserMerge(sourceUserId: UserA, targetUserId: UserB);

        _decorator.Entries.Should().Be(0);
        _decorator.UserHoldingsCacheStats.Entries.Should().Be(0);
    }

    [HumansFact]
    public async Task InvalidateAll_DropsProjectionAndPerUserEntries()
    {
        await SeedOneUserHolding();

        _decorator.InvalidateAll();

        _decorator.Entries.Should().Be(0);
        _decorator.UserHoldingsCacheStats.Entries.Should().Be(0);
    }

    private async Task SeedTwoUserHoldings()
    {
        UseNonCurrentEvent();
        SeedOrders(
            MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
                MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
            ]),
            MakeOrder(Guid.NewGuid(), matchedUserId: UserB, attendees: [
                MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.Valid),
            ]));

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _ = await _decorator.GetUserTicketHoldingsAsync(UserB);
        _decorator.Entries.Should().BeGreaterThan(0);
        _decorator.UserHoldingsCacheStats.Entries.Should().Be(2);
    }

    private async Task SeedOneUserHolding()
    {
        UseNonCurrentEvent();
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: UserA, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
        ]));

        _ = await _decorator.GetTicketOrdersAsync();
        _ = await _decorator.GetUserTicketHoldingsAsync(UserA);
        _decorator.Entries.Should().BeGreaterThan(0);
        _decorator.UserHoldingsCacheStats.Entries.Should().Be(1);
    }

    private void SeedOrders(params TicketOrder[] orders)
    {
        _repo.GetAllOrdersWithAttendeesAsync(Arg.Any<CancellationToken>())
            .Returns(orders);
    }

    private void UseNonCurrentEvent()
    {
        _repo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "ev_other" });
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
