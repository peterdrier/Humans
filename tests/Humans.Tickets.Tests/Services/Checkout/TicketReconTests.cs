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
    private readonly List<PlatformOrderSummary> _platform = [];
    private readonly List<TicketCheckoutTicket> _tickets = [];

    private TicketCheckoutOrder Order(
        Instant? createdAt = null,
        TicketCheckoutOrderState state = TicketCheckoutOrderState.Paid,
        long total = 30000,
        long refunded = 0,
        int tickets = 1,
        bool atPlatform = true,
        bool issue = true,
        string platformKey = Key)
    {
        var id = Guid.NewGuid();
        var order = new TicketCheckoutOrder
        {
            Id = id,
            TicketingSettingsId = Guid.NewGuid(),
            PlatformKey = platformKey,
            PlatformOrderId = $"ev/{id:N}",
            State = state,
            TotalMinor = total,
            RefundedMinor = refunded,
            CreatedAt = createdAt ?? Mid,
            Lines = Enumerable.Range(0, tickets).Select(_ => new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, AmountMinor = total / tickets })
                .Append(new TicketCheckoutOrderLine { Kind = OrderLineKind.Donation })
                .ToList(),
        };
        _orders.Add(order);
        if (atPlatform)
            _platform.Add(Mirror(order));
        if (issue && state == TicketCheckoutOrderState.Paid)
            _tickets.AddRange(Enumerable.Range(0, tickets).Select(i => new TicketCheckoutTicket { OrderId = id, LineIndex = i }));
        return order;
    }

    private static PlatformOrderSummary Mirror(TicketCheckoutOrder order) => new(
        order.PlatformOrderId!, order.Id, order.CreatedAt + Duration.FromSeconds(1),
        order.State == TicketCheckoutOrderState.Paid ? PlatformOrderState.Paid : PlatformOrderState.Expired,
        order.TotalMinor, order.Lines.Count(l => l.Kind == OrderLineKind.Ticket), order.RefundedMinor, 0);

    private TicketRecon Build(bool sharedAccount = false) =>
        TicketReconBuilder.Build(Key, From, To, _orders, _platform, _tickets, sharedAccount);

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outside_production_an_order_this_database_never_held_is_another_instances(bool sharedAccount)
    {
        Order();
        var elsewhere = Order(platformKey: "boxoffice", atPlatform: false);
        _platform.Add(new PlatformOrderSummary("ev/foreign", Guid.NewGuid(), Mid, PlatformOrderState.Paid, 5000, 1, 0, 0));
        _platform.Add(new PlatformOrderSummary("ev/unnamed", null, Mid, PlatformOrderState.Paid, 4000, 1, 0, 0));
        _platform.Add(new PlatformOrderSummary("ev/other-platform", elsewhere.Id, Mid, PlatformOrderState.Paid, 3000, 1, 0, 0));

        var recon = Build(sharedAccount);

        var extra = recon.Problems.Where(r => r.Issues.Contains(TicketReconIssue.ExtraAtPlatform)).Select(r => r.Platform!.PlatformOrderId);
        if (sharedAccount)
        {
            recon.OtherInstance.Select(b => b.PlatformOrderId).Should().Equal("ev/foreign");
            extra.Should().BeEquivalentTo("ev/unnamed", "ev/other-platform");
            recon.Platform.GrossMinor.Should().Be(30000 + 4000 + 3000);
        }
        else
        {
            recon.OtherInstance.Should().BeEmpty();
            extra.Should().BeEquivalentTo("ev/foreign", "ev/unnamed", "ev/other-platform");
        }
    }

    [HumansFact]
    public void Another_instances_orders_alone_balance_outside_production()
    {
        Order();
        _platform.Add(new PlatformOrderSummary("ev/foreign", Guid.NewGuid(), Mid, PlatformOrderState.Paid, 5000, 1, 0, 0));

        Build(sharedAccount: true).Balanced.Should().BeTrue();
        Build().Balanced.Should().BeFalse();
    }

    [HumansFact]
    public void Matching_sides_balance_and_count_paid_orders_only()
    {
        Order(tickets: 2);
        Order(refunded: 5000);
        Order(state: TicketCheckoutOrderState.Expired);

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Humans.Should().Be(new TicketReconTotals(2, 3, 60000, 5000, 0));
        recon.Platform.Should().Be(recon.Humans);
        recon.TicketsIssued.Should().Be(3);
        recon.Rows.Should().HaveCount(3);
    }

    [HumansFact]
    public void Order_the_platform_no_longer_lists_is_missing()
    {
        Order(atPlatform: false);

        var recon = Build();

        recon.Balanced.Should().BeFalse();
        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtPlatform);
        recon.Platform.Orders.Should().Be(0);
    }

    [HumansFact]
    public void Order_only_the_platform_holds_is_extra_and_counts_on_its_side()
    {
        Order();
        _platform.Add(new PlatformOrderSummary("ev/MANUAL", null, Mid, PlatformOrderState.Paid, 25000, 1, 0, 0));

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtPlatform);
        recon.Platform.Should().Be(new TicketReconTotals(2, 2, 55000, 0, 0));
        recon.Humans.Orders.Should().Be(1);
    }

    [HumansFact]
    public void Second_platform_order_for_one_humans_order_is_a_duplicate()
    {
        var order = Order();
        _platform.Add(Mirror(order) with { PlatformOrderId = "ev/AGAIN" });

        var recon = Build();

        recon.Problems.Should().ContainSingle().Which.Should().Match<TicketReconRow>(r =>
            r.Platform!.PlatformOrderId == "ev/AGAIN" && r.Issues.Single() == TicketReconIssue.Duplicate);
        recon.Platform.Orders.Should().Be(2);
        recon.Platform.GrossMinor.Should().Be(60000);
    }

    [HumansFact]
    public void Platform_order_humans_never_recorded_is_flagged_and_still_paired()
    {
        var order = Order(state: TicketCheckoutOrderState.Created);
        _platform[0] = _platform[0] with { State = PlatformOrderState.Paid };
        order.PlatformOrderId = null;

        var issues = Build().Problems.Single().Issues;

        issues.Should().BeEquivalentTo([TicketReconIssue.NotRecorded, TicketReconIssue.PaidMismatch]);
    }

    [HumansFact]
    public void Unopened_order_the_platform_never_took_is_left_out()
    {
        Order(state: TicketCheckoutOrderState.Created, atPlatform: false).PlatformOrderId = null;

        var recon = Build();

        recon.Rows.Should().BeEmpty();
        recon.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public void Free_order_no_platform_took_stays_out_of_the_totals()
    {
        Order();
        Order(total: 0, atPlatform: false).PlatformOrderId = null;

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Humans.Orders.Should().Be(1);
        recon.TicketsIssued.Should().Be(1);
    }

    [HumansFact]
    public void Charged_paid_order_no_platform_holds_is_missing_not_free()
    {
        Order(atPlatform: false).PlatformOrderId = null;

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtPlatform);
    }

    [HumansFact]
    public void Free_order_without_its_tickets_is_flagged()
    {
        Order(total: 0, atPlatform: false, issue: false).PlatformOrderId = null;

        Build().Problems.Single().Issues.Should().Equal(TicketReconIssue.TicketsNotIssued);
    }

    [HumansFact]
    public void Recorded_platform_order_replaced_by_another_names_both()
    {
        Order();
        _platform[0] = _platform[0] with { PlatformOrderId = "ev/OTHER" };

        var recon = Build();

        recon.Balanced.Should().BeFalse();
        recon.Problems.Select(r => (r.Platform?.PlatformOrderId, r.Issues.Single())).Should().BeEquivalentTo([
            ((string?)null, TicketReconIssue.MissingAtPlatform), ("ev/OTHER", TicketReconIssue.NotRecorded),
        ]);
    }

    [HumansFact]
    public void Difference_where_no_money_moved_is_listed_and_balanced()
    {
        Order(state: TicketCheckoutOrderState.Pending, atPlatform: false);
        _platform.Add(new PlatformOrderSummary("ev/LEFTOVER", null, Mid, PlatformOrderState.Expired, 25000, 1, 0, 0));

        var recon = Build();

        recon.Problems.Should().HaveCount(2).And.OnlyContain(r => !r.MoneyMoved);
        recon.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public void Differences_in_money_and_tickets_are_named_per_order()
    {
        Order();
        _platform[0] = _platform[0] with { TotalMinor = 29000, RefundedMinor = 1000, ChargedBackMinor = 500, TicketCount = 2 };

        Build().Problems.Single().Issues.Should().BeEquivalentTo([
            TicketReconIssue.TotalMismatch, TicketReconIssue.RefundMismatch, TicketReconIssue.ChargebackMismatch,
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
        _platform[0] = _platform[0] with { CreatedAt = To + Duration.FromMilliseconds(300) };
        var early = Order(createdAt: From - Duration.FromSeconds(1));
        _platform[1] = _platform[1] with { CreatedAt = From + Duration.FromMilliseconds(100) };

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Rows.Should().ContainSingle().Which.Order.Should().BeSameAs(late);
        early.CreatedAt.Should().BeLessThan(From);
    }

    [HumansFact]
    public void Platform_that_cannot_count_tickets_takes_humans_count()
    {
        Order(tickets: 3);
        _platform[0] = _platform[0] with { TicketCount = null };

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Platform.Tickets.Should().Be(3);
    }

    [HumansFact]
    public void Order_of_another_platform_is_not_this_ones_and_its_platform_twin_is_extra()
    {
        Order(platformKey: "boxoffice");

        var recon = Build();

        recon.Humans.Orders.Should().Be(0);
        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtPlatform);
    }

    [HumansFact]
    public void Unreadable_platform_is_never_balanced()
    {
        TicketReconBuilder.Unreadable(Key, From, To, "down").Balanced.Should().BeFalse();
    }
}
