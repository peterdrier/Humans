using Xunit;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Tests.Services;

public sealed class CachingTicketQueryServiceTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid UserC = Guid.NewGuid();

    private readonly ITicketManagement _inner;
    private readonly ITicketSyncService _sync = Substitute.For<ITicketSyncService>();
    private readonly IAttendeeContactImportService _import = Substitute.For<IAttendeeContactImportService>();
    private readonly FakeClock _clock;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _inner = Substitute.For<ITicketManagement>();

        var services = new ServiceCollection();
        services.AddKeyedScoped<ITicketManagement>(
            CachingTicketQueryService.InnerServiceKey,
            (_, _) => _inner);
        services.AddKeyedScoped<ITicketSyncService>(CachingTicketQueryService.InnerServiceKey, (_, _) => _sync);
        services.AddKeyedScoped<IAttendeeContactImportService>(CachingTicketQueryService.InnerServiceKey, (_, _) => _import);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _clock = new FakeClock(Instant.FromUtc(2026, 5, 1, 0, 0));

        _decorator = new CachingTicketQueryService(
            scopeFactory,
            _clock,
            NullLogger<CachingTicketQueryService>.Instance);

        SeedOrders();
        _inner.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserTicketHoldings(0, [])));
    }

    [HumansTheory]
    [InlineData("BC-Exact", true)]
    [InlineData(" BC-Exact ", true)]
    [InlineData("bc-exact", false)]
    [InlineData("old-event", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public async Task Barcode_lookup_uses_current_event_and_exact_code(string? barcode, bool found)
    {
        var hit = MakeAttendee(UserA, TicketAttendeeStatus.Void) with { Barcode = "BC-Exact" };
        var past = MakeOrder(Guid.NewGuid(), null,
            MakeAttendee(UserB, TicketAttendeeStatus.Valid) with { Barcode = "old-event" })
            with
        { IsCurrentEvent = false };
        SeedOrders(past, MakeOrder(Guid.NewGuid(), null, hit));

        var result = await _decorator.FindCurrentEventAttendeeByBarcodeAsync(
            barcode, TestContext.Current.CancellationToken);

        if (found) result.Should().BeSameAs(hit);
        else result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTicketOrdersAsync_ProjectionDerivesCurrentTicketHolders()
    {
        SeedOrders(MakeOrder(Guid.NewGuid(), matchedUserId: null, attendees: [
            MakeAttendee(matchedUserId: UserA, status: TicketAttendeeStatus.Valid),
            MakeAttendee(matchedUserId: UserB, status: TicketAttendeeStatus.CheckedIn),
            MakeAttendee(matchedUserId: UserC, status: TicketAttendeeStatus.Void),
        ]));

        var result = (await _decorator.GetTicketOrdersAsync(TestContext.Current.CancellationToken))
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

        var first = await _decorator.GetTicketOrdersAsync(TestContext.Current.CancellationToken);
        var second = await _decorator.GetTicketOrdersAsync(TestContext.Current.CancellationToken);

        first.Should().BeEquivalentTo(second);
        await _inner.Received(1).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserTicketHoldingsAsync_UsesTrackedUserCache()
    {
        SeedHoldings(UserA, new UserTicketHoldings(1, [], TicketCount: 1));

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA, TestContext.Current.CancellationToken);
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA, TestContext.Current.CancellationToken);

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

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA, TestContext.Current.CancellationToken);
        _clock.Advance(Duration.FromMinutes(6));
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA, TestContext.Current.CancellationToken);

        first.TicketCount.Should().Be(1);
        second.TicketCount.Should().Be(2);
        await _inner.Received(2).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GetUserTicketHoldingsAsync_LoadStartedBeforeEviction_DoesNotRepopulateCache(
        bool expired, bool viaSync)
    {
        var ct = TestContext.Current.CancellationToken;
        var old = new UserTicketHoldings(1, [], TicketCount: 1);
        var current = new UserTicketHoldings(0, []);
        var pending = new TaskCompletionSource<UserTicketHoldings>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (expired)
        {
            SeedHoldings(UserA, old);
            await _decorator.GetUserTicketHoldingsAsync(UserA, ct);
            _clock.Advance(Duration.FromMinutes(6));
        }
        _inner.GetUserTicketHoldingsAsync(UserA, ct).Returns(pending.Task, Task.FromResult(current));
        var read = _decorator.GetUserTicketHoldingsAsync(UserA, ct);

        if (viaSync) await _decorator.SyncAsync(ct);
        else await _decorator.EraseForUserAsync(UserA, ct);
        pending.SetResult(old);
        (await read).Should().BeSameAs(old);

        (await _decorator.GetUserTicketHoldingsAsync(UserA, ct)).Should().BeSameAs(current);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOrdersAndAttendeesAsync_DropsProjectionAndAllHoldings_EvenWhenALaterStageThrows(bool throws)
    {
        var ct = TestContext.Current.CancellationToken;
        await PrimeAsync(UserA, UserB);
        if (throws)
        {
            _sync.SyncAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromException<TicketSyncResult>(new InvalidOperationException("redemption failed")));
        }

        var sync = () => _decorator.SyncAsync(ct);
        if (throws) await sync.Should().ThrowAsync<InvalidOperationException>();
        else await sync();

        await AssertReloadedAsync(orders: true, reloaded: [UserA, UserB], kept: []);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyContactImport_DropsProjectionAndAllHoldings_EvenWhenParticipationThrows(bool throws)
    {
        var ct = TestContext.Current.CancellationToken;
        await PrimeAsync(UserA, UserB);
        var plan = new AttendeeImportPlan([], 0);
        if (throws)
        {
            _import.ApplyAsync(plan, Arg.Any<IReadOnlySet<Guid>>(), UserC, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<AttendeeImportResult>(new InvalidOperationException("participation failed")));
        }

        var apply = () => _decorator.ApplyAsync(plan, new HashSet<Guid>(), UserC, ct);
        if (throws) await apply.Should().ThrowAsync<InvalidOperationException>();
        else await apply();

        await AssertReloadedAsync(orders: true, reloaded: [UserA, UserB], kept: []);
    }

    [HumansFact]
    public async Task EraseForUserAsync_DropsProjectionAndAllHoldings()
    {
        await PrimeAsync(UserA, UserB);

        await _decorator.EraseForUserAsync(UserA, TestContext.Current.CancellationToken);

        await _inner.Received(1).EraseForUserAsync(UserA, Arg.Any<CancellationToken>());
        await AssertReloadedAsync(orders: true, reloaded: [UserA, UserB], kept: []);
    }

    [HumansFact]
    public async Task ReassignAsync_DropsProjectionAndBothUsersHoldings_LeavesOthersCached()
    {
        var now = Instant.FromUtc(2026, 5, 16, 12, 0);
        await PrimeAsync(UserA, UserB, UserC);

        await _decorator.ReassignAsync(UserA, UserB, UserC, now, TestContext.Current.CancellationToken);

        await _sync.Received(1).ReassignAsync(UserA, UserB, UserC, now, Arg.Any<CancellationToken>());
        await AssertReloadedAsync(orders: true, reloaded: [UserA, UserB], kept: [UserC]);
    }

    [HumansFact]
    public async Task SetPostEventHoldDays_DropsAllHoldings_KeepsProjection()
    {
        await PrimeAsync(UserA, UserB);

        await _decorator.SetPostEventHoldDaysAsync(60, UserC, TestContext.Current.CancellationToken);

        await _sync.Received(1).SetPostEventHoldDaysAsync(60, UserC, Arg.Any<CancellationToken>());
        await AssertReloadedAsync(orders: false, reloaded: [UserA, UserB], kept: []);
    }

    [HumansFact]
    public async Task ReadPassThroughs_KeepBothSlicesCached()
    {
        var ct = TestContext.Current.CancellationToken;
        await PrimeAsync(UserA);

        await _decorator.IsInErrorStateAsync(ct);
        await _decorator.ResetSyncStateForFullResyncAsync();
        await _decorator.BuildPlanAsync(ct);
        await _decorator.ContributeForUserAsync(UserA, ct);

        await AssertReloadedAsync(orders: false, reloaded: [], kept: [UserA]);
    }

    /// <summary>Loads the order projection and each user's holdings, then forgets those calls.</summary>
    private async Task PrimeAsync(params Guid[] users)
    {
        var ct = TestContext.Current.CancellationToken;
        SeedOrders(users.Select(u => MakeOrder(Guid.NewGuid(), matchedUserId: u, attendees: [
            MakeAttendee(matchedUserId: u, status: TicketAttendeeStatus.Valid),
        ])).ToArray());
        foreach (var user in users)
            SeedHoldings(user, new UserTicketHoldings(1, [], TicketCount: 1));

        _ = await _decorator.GetTicketOrdersAsync(ct);
        foreach (var user in users)
            _ = await _decorator.GetUserTicketHoldingsAsync(user, ct);
        _inner.ClearReceivedCalls();
    }

    /// <summary>Reads everything again and checks which reads reached the inner service.</summary>
    private async Task AssertReloadedAsync(bool orders, Guid[] reloaded, Guid[] kept)
    {
        var ct = TestContext.Current.CancellationToken;
        _ = await _decorator.GetTicketOrdersAsync(ct);
        foreach (var user in reloaded.Concat(kept))
            _ = await _decorator.GetUserTicketHoldingsAsync(user, ct);

        await _inner.Received(orders ? 1 : 0).GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        foreach (var user in reloaded)
            await _inner.Received(1).GetUserTicketHoldingsAsync(user, Arg.Any<CancellationToken>());
        foreach (var user in kept)
            await _inner.DidNotReceive().GetUserTicketHoldingsAsync(user, Arg.Any<CancellationToken>());
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
            BuyerEmail: "buyer@example.com",
            TotalAmount: 100m,
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
            AttendeeName: "Attendee",
            AttendeeEmail: email,
            TicketTypeName: "Full Week",
            Status: status,
            MatchedUserId: matchedUserId);
}
