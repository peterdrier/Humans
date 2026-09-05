using AwesomeAssertions;
using Humans.TicketTailor.Services;
using NodaTime;

namespace Humans.TicketTailor.Tests.Services;

/// <summary>The stub dataset is deterministic and shaped like a real event.</summary>
public class StubTicketVendorServiceTests
{
    private static readonly CancellationToken Ct = Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task FirstOrder_IsTheRecognizableTestUser()
    {
        var orders = await new StubTicketVendorService().GetOrdersAsync(null, "stub-event", Ct);

        orders[0].BuyerEmail.Should().Be("peter@nobodies.team");
        orders[0].Tickets.Should().OnlyContain(t => string.Equals(t.AttendeeEmail, "peter@nobodies.team", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Dataset_Is450PaidOrdersOf600ValidTicketsPlusFourNonPaidOrders()
    {
        var stub = new StubTicketVendorService();
        var orders = await stub.GetOrdersAsync(null, "stub-event", Ct);
        var tickets = await stub.GetIssuedTicketsAsync(null, "stub-event", Ct);

        orders.Count(o => string.Equals(o.PaymentStatus, "completed", StringComparison.Ordinal)).Should().Be(450);
        orders.Count(o => !string.Equals(o.PaymentStatus, "completed", StringComparison.Ordinal)).Should().Be(4);
        tickets.Count(t => string.Equals(t.Status, "valid", StringComparison.Ordinal)).Should().Be(600);
        tickets.Count(t => string.Equals(t.Status, "void", StringComparison.Ordinal)).Should().Be(4);
        tickets.Select(t => t.VendorOrderId).Should().BeSubsetOf(orders.Select(o => o.VendorOrderId));
    }

    [HumansFact]
    public async Task CheckIns_FallOnTheGateDayAndReferenceValidTickets()
    {
        var stub = new StubTicketVendorService();
        var tickets = await stub.GetIssuedTicketsAsync(null, "stub-event", Ct);
        var checkIns = await stub.GetCheckInsAsync(null, "stub-event", Ct);

        var validIds = tickets.Where(t => string.Equals(t.Status, "valid", StringComparison.Ordinal)).Select(t => t.VendorTicketId).ToHashSet(StringComparer.Ordinal);
        checkIns.Should().NotBeEmpty();
        checkIns.Should().OnlyContain(c => c.CheckedInAt.InUtc().Date == new LocalDate(2026, 7, 8));
        checkIns.Should().OnlyContain(c => validIds.Contains(c.VendorTicketId));
    }

    [HumansFact]
    public async Task IncrementalSync_ReturnsNoTicketsAndNoCheckIns()
    {
        var stub = new StubTicketVendorService();
        var since = Instant.FromUtc(2026, 1, 1, 0, 0);

        (await stub.GetIssuedTicketsAsync(since, "stub-event", Ct)).Should().BeEmpty();
        (await stub.GetCheckInsAsync(since, "stub-event", Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Void_MutatesOnlyThisInstance()
    {
        var first = new StubTicketVendorService();
        var ticketId = (await first.GetIssuedTicketsAsync(null, "stub-event", Ct))[0].VendorTicketId;

        await first.VoidIssuedTicketAsync(ticketId, voidToHold: false, Ct);

        (await first.GetIssuedTicketsAsync(null, "stub-event", Ct))
            .Single(t => string.Equals(t.VendorTicketId, ticketId, StringComparison.Ordinal)).Status.Should().Be("voided");
        (await new StubTicketVendorService().GetIssuedTicketsAsync(null, "stub-event", Ct))
            .Single(t => string.Equals(t.VendorTicketId, ticketId, StringComparison.Ordinal)).Status.Should().Be("valid");
    }
}
