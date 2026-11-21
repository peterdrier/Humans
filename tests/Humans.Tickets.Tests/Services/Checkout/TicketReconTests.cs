using AwesomeAssertions;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using NodaTime;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketReconTests
{
    private const string Key = "pretix";
    private static readonly Instant From = Instant.FromUtc(2027, 1, 1, 0, 0);
    private static readonly Instant To = Instant.FromUtc(2027, 2, 1, 0, 0);
    private static readonly Instant Mid = Instant.FromUtc(2027, 1, 15, 12, 0);

    private readonly List<TicketCheckoutOrder> _orders = [];
    private readonly List<BackendOrderSummary> _backend = [];
    private readonly List<TicketCheckoutTicket> _tickets = [];

    private TicketCheckoutOrder Order(
        Instant? createdAt = null,
        TicketCheckoutOrderState state = TicketCheckoutOrderState.Paid,
        long total = 30000,
        long refunded = 0,
        int tickets = 1,
        bool atBackend = true,
        bool issue = true,
        string backendKey = Key)
    {
        var id = Guid.NewGuid();
        var order = new TicketCheckoutOrder
        {
            Id = id,
            SalesEventId = Guid.NewGuid(),
            BackendKey = backendKey,
            BackendOrderId = $"ev/{id:N}",
            State = state,
            TotalMinor = total,
            RefundedMinor = refunded,
            CreatedAt = createdAt ?? Mid,
            Lines = Enumerable.Range(0, tickets).Select(_ => new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Ticket, AmountMinor = total / tickets })
                .Append(new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Donation })
                .ToList(),
        };
        _orders.Add(order);
        if (atBackend)
            _backend.Add(Mirror(order));
        if (issue && state == TicketCheckoutOrderState.Paid)
            _tickets.AddRange(Enumerable.Range(0, tickets).Select(i => new TicketCheckoutTicket { OrderId = id, LineIndex = i }));
        return order;
    }

    private static BackendOrderSummary Mirror(TicketCheckoutOrder order) => new(
        order.BackendOrderId!, order.Id, order.CreatedAt + Duration.FromSeconds(1),
        order.State == TicketCheckoutOrderState.Paid ? CheckoutState.Paid : CheckoutState.Expired,
        order.TotalMinor, order.Lines.Count(l => l.Kind == CheckoutLineKind.Ticket), order.RefundedMinor, 0);

    private TicketRecon Build() => TicketReconBuilder.Build(Key, From, To, _orders, _backend, _tickets);

    [HumansFact]
    public void Matching_sides_balance_and_count_paid_orders_only()
    {
        Order(tickets: 2);
        Order(refunded: 5000);
        Order(state: TicketCheckoutOrderState.Expired);

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Humans.Should().Be(new TicketReconTotals(2, 3, 60000, 5000, 0));
        recon.Backend.Should().Be(recon.Humans);
        recon.TicketsIssued.Should().Be(3);
        recon.Rows.Should().HaveCount(3);
    }

    [HumansFact]
    public void Order_the_backend_no_longer_lists_is_missing()
    {
        Order(atBackend: false);

        var recon = Build();

        recon.Balanced.Should().BeFalse();
        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtBackend);
        recon.Backend.Orders.Should().Be(0);
    }

    [HumansFact]
    public void Order_only_the_backend_holds_is_extra_and_counts_on_its_side()
    {
        Order();
        _backend.Add(new BackendOrderSummary("ev/MANUAL", null, Mid, CheckoutState.Paid, 25000, 1, 0, 0));

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtBackend);
        recon.Backend.Should().Be(new TicketReconTotals(2, 2, 55000, 0, 0));
        recon.Humans.Orders.Should().Be(1);
    }

    [HumansFact]
    public void Second_backend_order_for_one_humans_order_is_a_duplicate()
    {
        var order = Order();
        _backend.Add(Mirror(order) with { BackendOrderId = "ev/AGAIN" });

        var recon = Build();

        recon.Problems.Should().ContainSingle().Which.Should().Match<TicketReconRow>(r =>
            r.Backend!.BackendOrderId == "ev/AGAIN" && r.Issues.Single() == TicketReconIssue.Duplicate);
        recon.Backend.Orders.Should().Be(2);
        recon.Backend.GrossMinor.Should().Be(60000);
    }

    [HumansFact]
    public void Backend_order_humans_never_recorded_is_flagged_and_still_paired()
    {
        var order = Order(state: TicketCheckoutOrderState.Created);
        _backend[0] = _backend[0] with { State = CheckoutState.Paid };
        order.BackendOrderId = null;

        var issues = Build().Problems.Single().Issues;

        issues.Should().BeEquivalentTo([TicketReconIssue.NotRecorded, TicketReconIssue.PaidMismatch]);
    }

    [HumansFact]
    public void Unopened_order_the_backend_never_took_is_left_out()
    {
        Order(state: TicketCheckoutOrderState.Created, atBackend: false).BackendOrderId = null;

        var recon = Build();

        recon.Rows.Should().BeEmpty();
        recon.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public void Free_order_no_backend_took_stays_out_of_the_totals()
    {
        Order();
        Order(total: 0, atBackend: false).BackendOrderId = null;

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Humans.Orders.Should().Be(1);
        recon.TicketsIssued.Should().Be(1);
    }

    [HumansFact]
    public void Free_order_without_its_tickets_is_flagged()
    {
        Order(total: 0, atBackend: false, issue: false).BackendOrderId = null;

        Build().Problems.Single().Issues.Should().Equal(TicketReconIssue.TicketsNotIssued);
    }

    [HumansFact]
    public void Differences_in_money_and_tickets_are_named_per_order()
    {
        Order();
        _backend[0] = _backend[0] with { TotalMinor = 29000, RefundedMinor = 1000, DisputedMinor = 500, TicketCount = 2 };

        Build().Problems.Single().Issues.Should().BeEquivalentTo([
            TicketReconIssue.TotalMismatch, TicketReconIssue.RefundMismatch, TicketReconIssue.DisputeMismatch,
            TicketReconIssue.TicketCountMismatch,
        ]);
    }

    [HumansFact]
    public void Paid_order_without_its_tickets_is_flagged()
    {
        Order(tickets: 2, issue: false);

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.TicketsNotIssued);
        recon.TicketsIssued.Should().Be(0);
        recon.Balanced.Should().BeFalse();
    }

    [HumansFact]
    public void Pair_split_by_the_period_edge_belongs_to_the_humans_order()
    {
        var late = Order(createdAt: To - Duration.FromMilliseconds(200));
        _backend[0] = _backend[0] with { CreatedAt = To + Duration.FromMilliseconds(300) };
        var early = Order(createdAt: From - Duration.FromSeconds(1));
        _backend[1] = _backend[1] with { CreatedAt = From + Duration.FromMilliseconds(100) };

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Rows.Should().ContainSingle().Which.Order.Should().BeSameAs(late);
        early.CreatedAt.Should().BeLessThan(From);
    }

    [HumansFact]
    public void Backend_that_cannot_count_tickets_takes_humans_count()
    {
        Order(tickets: 3);
        _backend[0] = _backend[0] with { TicketCount = null };

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Backend.Tickets.Should().Be(3);
    }

    [HumansFact]
    public void Order_of_another_backend_is_not_this_ones_and_its_backend_twin_is_extra()
    {
        Order(backendKey: "boxoffice");

        var recon = Build();

        recon.Humans.Orders.Should().Be(0);
        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtBackend);
    }

    [HumansFact]
    public void Unreadable_backend_is_never_balanced()
    {
        TicketReconBuilder.Unreadable(Key, From, To, "down").Balanced.Should().BeFalse();
    }
}
