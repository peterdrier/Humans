using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Tickets.Tests.Services;

public sealed class CachingTicketVendorServiceTests
{
    private readonly ITicketVendorService _inner;
    private readonly FakeClock _clock;
    private readonly CachingTicketVendorService _decorator;

    public CachingTicketVendorServiceTests()
    {
        _inner = Substitute.For<ITicketVendorService>();

        var services = new ServiceCollection();
        services.AddKeyedScoped<ITicketVendorService>(
            TicketVendorServiceKeys.InnerServiceKey,
            (_, _) => _inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _clock = new FakeClock(Instant.FromUtc(2026, 5, 1, 0, 0));

        _decorator = new CachingTicketVendorService(
            scopeFactory,
            _clock,
            NullLogger<CachingTicketVendorService>.Instance);
    }

    private static VendorEventSummaryDto MakeSummary(string eventId, int sold = 10) =>
        new(EventId: eventId, EventName: "Test Event", TotalCapacity: 100, TicketsSold: sold, TicketsRemaining: 100 - sold);

    [HumansFact]
    public async Task GetEventSummaryAsync_Miss_CallsInnerOnceAndReturnsItsValue()
    {
        var summary = MakeSummary("ev_test");
        _inner.GetEventSummaryAsync("ev_test", Arg.Any<CancellationToken>()).Returns(Task.FromResult(summary));

        var result = await _decorator.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(summary);
        await _inner.Received(1).GetEventSummaryAsync("ev_test", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetEventSummaryAsync_Hit_DoesNotCallInnerAgain()
    {
        _inner.GetEventSummaryAsync("ev_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeSummary("ev_test")));

        var first = await _decorator.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);
        var second = await _decorator.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        second.Should().Be(first);
        await _inner.Received(1).GetEventSummaryAsync("ev_test", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateEventSummary_CausesNextCallToHitInnerAgain()
    {
        _inner.GetEventSummaryAsync("ev_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeSummary("ev_test", sold: 1)), Task.FromResult(MakeSummary("ev_test", sold: 2)));

        var first = await _decorator.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);
        _decorator.InvalidateEventSummary("ev_test");
        var second = await _decorator.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        first.TicketsSold.Should().Be(1);
        second.TicketsSold.Should().Be(2);
        await _inner.Received(2).GetEventSummaryAsync("ev_test", Arg.Any<CancellationToken>());
    }

    // Every other ITicketVendorService member forwards straight through, uncached — the
    // decorator must not silently swallow one. One test per member, matching the port's
    // exact signature, catches a member missing from WithInner as surely as a generic
    // reflection sweep would, without needing to synthesize arbitrary argument values.

    [HumansFact]
    public async Task GetOrdersAsync_ForwardsToInner()
    {
        var orders = new List<VendorOrderDto>();
        _inner.GetOrdersAsync(null, "ev_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VendorOrderDto>>(orders));

        var result = await _decorator.GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeSameAs(orders);
        await _inner.Received(1).GetOrdersAsync(null, "ev_test", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetIssuedTicketsAsync_ForwardsToInner()
    {
        var tickets = new List<VendorTicketDto>();
        _inner.GetIssuedTicketsAsync(null, "ev_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VendorTicketDto>>(tickets));

        var result = await _decorator.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeSameAs(tickets);
        await _inner.Received(1).GetIssuedTicketsAsync(null, "ev_test", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCheckInsAsync_ForwardsToInner()
    {
        var checkIns = new List<VendorCheckInDto>();
        _inner.GetCheckInsAsync(null, "ev_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VendorCheckInDto>>(checkIns));

        var result = await _decorator.GetCheckInsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeSameAs(checkIns);
        await _inner.Received(1).GetCheckInsAsync(null, "ev_test", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GenerateDiscountCodesAsync_ForwardsToInner()
    {
        var spec = new DiscountCodeSpec(1, DiscountType.Fixed, 10m, null);
        var codes = new List<string> { "CODE-1" };
        _inner.GenerateDiscountCodesAsync(spec, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(codes));

        var result = await _decorator.GenerateDiscountCodesAsync(spec, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeSameAs(codes);
        await _inner.Received(1).GenerateDiscountCodesAsync(spec, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_ForwardsToInner()
    {
        var expected = new VoidIssuedTicketResult("tkt_1", null);
        _inner.VoidIssuedTicketAsync("tkt_1", true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var result = await _decorator.VoidIssuedTicketAsync("tkt_1", true, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(expected);
        await _inner.Received(1).VoidIssuedTicketAsync("tkt_1", true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task IssueTicketAsync_ForwardsToInner()
    {
        var request = new IssueTicketRequest("ev_1", "tt_1", null, "Jane Doe", null, false, null);
        var expected = new VendorTicketDto("tkt_1", null, "Jane Doe", null, "Full Week", 0m, "valid");
        _inner.IssueTicketAsync(request, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var result = await _decorator.IssueTicketAsync(request, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(expected);
        await _inner.Received(1).IssueTicketAsync(request, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateCheckInAsync_ForwardsToInner()
    {
        var occurredAt = Instant.FromUtc(2026, 7, 8, 12, 0);
        _inner.CreateCheckInAsync("tkt_1", occurredAt, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _decorator.CreateCheckInAsync("tkt_1", occurredAt, Xunit.TestContext.Current.CancellationToken);

        await _inner.Received(1).CreateCheckInAsync("tkt_1", occurredAt, Arg.Any<CancellationToken>());
    }
}
