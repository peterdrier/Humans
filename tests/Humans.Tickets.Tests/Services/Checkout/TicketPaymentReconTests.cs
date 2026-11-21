using AwesomeAssertions;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using NodaTime;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketPaymentReconTests
{
    private const string Key = "boxoffice";
    private static readonly Instant From = Instant.FromUtc(2027, 1, 1, 0, 0);
    private static readonly Instant To = Instant.FromUtc(2027, 2, 1, 0, 0);
    private static readonly Instant Mid = Instant.FromUtc(2027, 1, 15, 12, 0);

    private readonly List<TicketCheckoutOrder> _orders = [];
    private readonly List<PaymentLedgerEntry> _entries = [];
    private readonly List<PaymentPayout> _payouts = [];

    private TicketCheckoutOrder Order(long total = 30000, long? fee = 450, TicketCheckoutOrderState state = TicketCheckoutOrderState.Paid,
        string backendKey = Key)
    {
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            BackendKey = backendKey,
            State = state,
            TotalMinor = total,
            FeeMinor = fee,
            CreatedAt = Mid,
        };
        _orders.Add(order);
        return order;
    }

    private void Entry(PaymentLedgerEntryKind kind, long amount, long fee = 0, Guid? orderId = null) =>
        _entries.Add(new PaymentLedgerEntry($"txn_{_entries.Count}", Mid, kind, amount, fee, orderId, null));

    private TicketPaymentRecon Build(bool sharedAccount = false) =>
        TicketPaymentReconBuilder.Build(Key, From, To, new PaymentLedger(_entries, _payouts), _orders, sharedAccount: sharedAccount);

    [HumansFact]
    public void Outside_production_a_movement_naming_an_order_this_database_never_held_is_set_apart_but_counted()
    {
        var elsewhere = Order(backendKey: "pretix");
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, Guid.NewGuid());
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450);
        Entry(PaymentLedgerEntryKind.Refund, -5000, 0, elsewhere.Id);

        var shared = Build(sharedAccount: true);
        shared.OtherInstance.Select(e => e.Id).Should().Equal("txn_0");
        shared.Problems.Should().HaveCount(2).And.OnlyContain(r => r.Issues.SequenceEqual(new[] { TicketPaymentIssue.NoOrder }));
        shared.Totals.ChargedMinor.Should().Be(60000);

        var production = Build();
        production.OtherInstance.Should().BeEmpty();
        production.Problems.Should().HaveCount(3);
    }

    [HumansFact]
    public void Another_instances_payments_alone_balance_outside_production()
    {
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, Guid.NewGuid());

        Build(sharedAccount: true).Balanced.Should().BeTrue();
        Build().Balanced.Should().BeFalse();
    }

    [HumansFact]
    public void Payments_matching_their_orders_with_the_recorded_fee_balance()
    {
        var order = Order();
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, order.Id);
        Entry(PaymentLedgerEntryKind.Refund, -10000, 0, order.Id);
        Entry(PaymentLedgerEntryKind.Payout, -19550);
        _payouts.Add(new PaymentPayout("po_1", Mid, Mid, 19550, "paid", 19550, 2));

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Totals.Should().Be(new TicketPaymentTotals(30000, 10000, 0, 450, 0, 19550));
        recon.Totals.NetMinor.Should().Be(19550);
        recon.PaymentFeesMinor.Should().Be(450);
        recon.HumansPaymentFeesMinor.Should().Be(450);
    }

    [HumansFact]
    public void A_fee_other_than_the_one_Humans_recorded_unbalances()
    {
        var known = Order(fee: 450);
        var unknown = Order(fee: null);
        Entry(PaymentLedgerEntryKind.Charge, 30000, 470, known.Id);
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, unknown.Id);

        var recon = Build();

        recon.Balanced.Should().BeFalse();
        recon.Problems.Should().HaveCount(2).And.OnlyContain(r => r.Issues.SequenceEqual(new[] { TicketPaymentIssue.FeeMismatch }));
        recon.PaymentFeesMinor.Should().Be(920);
        recon.HumansPaymentFeesMinor.Should().Be(450);
    }

    [HumansFact]
    public void A_payment_for_another_amount_or_an_unpaid_order_unbalances()
    {
        var expired = Order(state: TicketCheckoutOrderState.Expired);
        var settling = Order(state: TicketCheckoutOrderState.Processing);
        Entry(PaymentLedgerEntryKind.Charge, 25000, 450, expired.Id);
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, settling.Id);

        var recon = Build();

        recon.Problems.Should().ContainSingle().Which.Issues.Should()
            .Equal(TicketPaymentIssue.NotPaidInHumans, TicketPaymentIssue.AmountMismatch);
    }

    [HumansFact]
    public void Money_naming_no_order_on_this_backend_unbalances()
    {
        var elsewhere = Order(backendKey: "pretix");
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450);
        Entry(PaymentLedgerEntryKind.Refund, -5000, 0, elsewhere.Id);
        Entry(PaymentLedgerEntryKind.Dispute, -30000, 1500, Guid.NewGuid());

        var recon = Build();

        recon.Problems.Should().HaveCount(3).And.OnlyContain(r => r.Issues.SequenceEqual(new[] { TicketPaymentIssue.NoOrder }));
    }

    [HumansFact]
    public void Fees_on_their_own_are_listed_and_counted_but_do_not_unbalance()
    {
        var order = Order();
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, order.Id);
        Entry(PaymentLedgerEntryKind.Dispute, -30000, 1500, order.Id);
        Entry(PaymentLedgerEntryKind.Fee, -200);
        Entry(PaymentLedgerEntryKind.Charge, -30000, 0, order.Id);

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.OtherFees.Select(r => r.Entry.Kind).Should().Equal(PaymentLedgerEntryKind.Dispute, PaymentLedgerEntryKind.Fee);
        recon.Totals.FeesMinor.Should().Be(450 + 1500 + 200);
        recon.Totals.ChargedMinor.Should().Be(0);
        recon.Totals.DisputedMinor.Should().Be(30000);
    }

    [HumansFact]
    public void A_payout_that_does_not_add_up_or_failed_unbalances()
    {
        _payouts.Add(new PaymentPayout("po_short", Mid, Mid, 10000, "paid", 9999, 3));
        _payouts.Add(new PaymentPayout("po_failed", Mid, null, 5000, "failed", 5000, 1));
        _payouts.Add(new PaymentPayout("po_manual", Mid, Mid, 7000, "in_transit", null, 0));

        var recon = Build();

        recon.Balanced.Should().BeFalse();
        recon.PayoutProblems.Select(p => p.Payout.Id).Should().Equal("po_short");
        recon.Payouts.Single(p => string.Equals(p.Payout.Id, "po_failed", StringComparison.Ordinal)).Issues.Should().Equal(TicketPayoutIssue.Failed);
    }

    [HumansFact]
    public void A_failed_payout_alone_is_shown_but_balances()
    {
        _payouts.Add(new PaymentPayout("po_failed", Mid, null, 5000, "failed", 5000, 1));

        Build().Balanced.Should().BeTrue();
    }

    [HumansFact]
    public void A_failed_delayed_debit_whose_payment_came_back_balances()
    {
        var failed = Order(state: TicketCheckoutOrderState.Failed);
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, failed.Id);
        Entry(PaymentLedgerEntryKind.Charge, -30000, 0, failed.Id);

        var recon = Build();

        recon.Balanced.Should().BeTrue();
        recon.Totals.ChargedMinor.Should().Be(0);
    }

    [HumansFact]
    public void A_payment_kept_on_a_failed_order_unbalances()
    {
        var failed = Order(state: TicketCheckoutOrderState.Failed);
        Entry(PaymentLedgerEntryKind.Charge, 30000, 450, failed.Id);

        Build().Problems.Should().ContainSingle().Which.Issues.Should().Equal(TicketPaymentIssue.NotPaidInHumans);
    }

    [HumansFact]
    public void An_unreadable_account_never_balances()
    {
        TicketPaymentReconBuilder.Unreadable(Key, From, To, "down").Balanced.Should().BeFalse();
    }
}
