using AwesomeAssertions;
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
    public void Compare_UnknownOrderOutsideProduction_IsOtherInstances(bool sharedAccount)
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
    public void Compare_OtherInstanceOrders_BalanceOutsideProduction()
    {
        Order();
        _platform.Add(new PlatformOrderSummary("ev/foreign", Guid.NewGuid(), Mid, PlatformOrderState.Paid, 5000, 1, 0, 0));

        Build(sharedAccount: true).Balanced.Should().BeTrue();
        Build().Balanced.Should().BeFalse();
    }

    [HumansFact]
    public void Compare_MatchingSides_Balance()
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
    public void Compare_OrderGoneFromPlatform_IsMissing()
    {
        Order(atPlatform: false);

        var recon = Build();

        recon.Balanced.Should().BeFalse();
        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtPlatform);
        recon.Platform.Orders.Should().Be(0);
    }

    [HumansFact]
    public void Compare_PlatformOnlyOrder_IsExtra()
    {
        Order();
        _platform.Add(new PlatformOrderSummary("ev/MANUAL", null, Mid, PlatformOrderState.Paid, 25000, 1, 0, 0));

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtPlatform);
        recon.Platform.Should().Be(new TicketReconTotals(2, 2, 55000, 0, 0));
        recon.Humans.Orders.Should().Be(1);
    }

    [HumansFact]
    public void Compare_SecondPlatformOrder_IsDuplicate()
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
    public void Compare_UnrecordedPlatformOrder_IsFlagged()
    {
        var order = Order(state: TicketCheckoutOrderState.Created);
        _platform[0] = _platform[0] with { State = PlatformOrderState.Paid };
        order.PlatformOrderId = null;

        var issues = Build().Problems.Single().Issues;

        issues.Should().BeEquivalentTo([TicketReconIssue.NotRecorded, TicketReconIssue.PaidMismatch]);
    }

    [HumansFact]
    public void Compare_UnopenedOrder_IsLeftOut()
    {
        Order(state: TicketCheckoutOrderState.Created, atPlatform: false).PlatformOrderId = null;

        var recon = Build();

        recon.Rows.Should().BeEmpty();
        recon.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public void Compare_FreeOrderNotOnPlatform_IsLeftOut()
    {
        Order();
        Order(total: 0, atPlatform: false).PlatformOrderId = null;

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Humans.Orders.Should().Be(1);
        recon.TicketsIssued.Should().Be(1);
    }

    [HumansFact]
    public void Compare_ChargedOrderNotOnPlatform_IsMissing()
    {
        Order(atPlatform: false).PlatformOrderId = null;

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtPlatform);
    }

    [HumansFact]
    public void Compare_FreeOrderNoTickets_IsFlagged()
    {
        Order(total: 0, atPlatform: false, issue: false).PlatformOrderId = null;

        Build().Problems.Single().Issues.Should().Equal(TicketReconIssue.TicketsNotIssued);
    }

    [HumansFact]
    public void Compare_PlatformOrderReplaced_NamesBoth()
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
    public void Compare_DifferenceWithoutMoney_Balances()
    {
        Order(state: TicketCheckoutOrderState.Pending, atPlatform: false);
        _platform.Add(new PlatformOrderSummary("ev/LEFTOVER", null, Mid, PlatformOrderState.Expired, 25000, 1, 0, 0));

        var recon = Build();

        recon.Problems.Should().HaveCount(2).And.OnlyContain(r => !r.MoneyMoved);
        recon.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public void Compare_Differences_AreNamedPerOrder()
    {
        Order();
        _platform[0] = _platform[0] with { TotalMinor = 29000, RefundedMinor = 1000, ChargedBackMinor = 500, TicketCount = 2 };

        Build().Problems.Single().Issues.Should().BeEquivalentTo([
            TicketReconIssue.TotalMismatch, TicketReconIssue.RefundMismatch, TicketReconIssue.ChargebackMismatch,
            TicketReconIssue.TicketCountMismatch,
        ]);
    }

    [HumansFact]
    public void Compare_PaidOrderNoTickets_IsFlagged()
    {
        Order(tickets: 2, issue: false);

        var recon = Build();

        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.TicketsNotIssued);
        recon.TicketsIssued.Should().Be(0);
        recon.Balanced.Should().BeFalse();
    }

    [HumansFact]
    public void Compare_PairSplitByPeriodEdge_FollowsHumansOrder()
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
    public void Compare_PlatformWithoutTicketCount_UsesHumansCount()
    {
        Order(tickets: 3);
        _platform[0] = _platform[0] with { TicketCount = null };

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Platform.Tickets.Should().Be(3);
    }

    [HumansFact]
    public void Compare_OtherPlatformOrder_TwinIsExtra()
    {
        Order(platformKey: "boxoffice");

        var recon = Build();

        recon.Humans.Orders.Should().Be(0);
        recon.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtPlatform);
    }

    [HumansFact]
    public void Compare_UnreadablePlatform_NeverBalances()
    {
        TicketReconBuilder.Unreadable(Key, From, To, "down").Balanced.Should().BeFalse();
    }
}
