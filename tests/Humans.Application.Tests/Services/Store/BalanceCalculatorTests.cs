using AwesomeAssertions;
using Humans.Application.Services.Store;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Tests.Services.Store;

public class BalanceCalculatorTests
{
    [HumansFact]
    public void Empty_order_has_zero_balance()
    {
        var order = new StoreOrder();
        var result = BalanceCalculator.Compute(order);
        result.LinesSubtotalEur.Should().Be(0);
        result.VatTotalEur.Should().Be(0);
        result.DepositTotalEur.Should().Be(0);
        result.PaymentsTotalEur.Should().Be(0);
        result.BalanceEur.Should().Be(0);
        result.Lines.Should().BeEmpty();
    }

    [HumansFact]
    public void Single_line_with_21_percent_vat()
    {
        var lineId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Id = lineId, Qty = 2, UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };

        var r = BalanceCalculator.Compute(order);
        r.LinesSubtotalEur.Should().Be(100m);
        r.VatTotalEur.Should().Be(21m);
        r.DepositTotalEur.Should().Be(0m);
        r.BalanceEur.Should().Be(121m);
        r.Lines.Should().ContainSingle().Which.Should().Be(
            new BalanceCalculator.LineTotals(lineId, 50m, 21m, null, 100m, 21m, 0m, 121m));
    }

    [HumansFact]
    public void Deposit_lines_excluded_from_vat_added_to_total()
    {
        var lineId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Id = lineId, Qty = 1, UnitPriceSnapshot = 30m, VatRateSnapshot = 21m, DepositAmountSnapshot = 100m }
            }
        };

        var r = BalanceCalculator.Compute(order);
        r.LinesSubtotalEur.Should().Be(30m);
        r.VatTotalEur.Should().Be(6.30m);
        r.DepositTotalEur.Should().Be(100m);
        r.BalanceEur.Should().Be(136.30m);
        r.Lines.Should().ContainSingle().Which.Should().Be(
            new BalanceCalculator.LineTotals(lineId, 30m, 21m, 100m, 30m, 6.30m, 100m, 136.30m));
    }

    [HumansFact]
    public void Payments_reduce_balance_negative_payment_treated_as_refund()
    {
        var lineId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Id = lineId, Qty = 1, UnitPriceSnapshot = 100m, VatRateSnapshot = 21m }
            },
            Payments = new List<StorePayment>
            {
                new() { AmountEur = 50m, Method = StorePaymentMethod.Stripe },
                new() { AmountEur = -10m, Method = StorePaymentMethod.Manual }
            }
        };

        var r = BalanceCalculator.Compute(order);
        r.PaymentsTotalEur.Should().Be(40m);
        r.BalanceEur.Should().Be(81m);
        r.Lines.Should().ContainSingle().Which.Should().Be(
            new BalanceCalculator.LineTotals(lineId, 100m, 21m, null, 100m, 21m, 0m, 121m));
    }

    // ── Payment status exclusion (nobodies-collective/Humans#638) ───────────────
    // Only Paid rows count. A Pending mandate (captured, not yet cleared) and a Failed
    // settlement (bounced) must NOT reduce the order balance.

    [HumansFact]
    public void Only_paid_payments_count_toward_balance()
    {
        var lineId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Id = lineId, Qty = 1, UnitPriceSnapshot = 100m, VatRateSnapshot = 0m }
            },
            Payments = new List<StorePayment>
            {
                new() { AmountEur = 40m, Method = StorePaymentMethod.Stripe, Status = StorePaymentStatus.Paid },
                new() { AmountEur = 30m, Method = StorePaymentMethod.Stripe, Status = StorePaymentStatus.Pending },
                new() { AmountEur = 25m, Method = StorePaymentMethod.Stripe, Status = StorePaymentStatus.Failed }
            }
        };

        var r = BalanceCalculator.Compute(order);

        r.PaymentsTotalEur.Should().Be(40m);   // only the Paid row
        r.BalanceEur.Should().Be(60m);          // 100 due − 40 cleared
    }

    [HumansFact]
    public void Pending_only_order_is_not_treated_as_paid()
    {
        // SEPA mandate captured at checkout but not yet cleared: the order is still fully owed.
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), Qty = 1, UnitPriceSnapshot = 50m, VatRateSnapshot = 0m }
            },
            Payments = new List<StorePayment>
            {
                new() { AmountEur = 50m, Method = StorePaymentMethod.Stripe, Status = StorePaymentStatus.Pending }
            }
        };

        var r = BalanceCalculator.Compute(order);

        r.PaymentsTotalEur.Should().Be(0m);
        r.BalanceEur.Should().Be(50m); // mandate ≠ payment — still fully owed
    }

    // ── Repricing (nobodies-collective/Humans#816) ──────────────────────────────
    // Open orders are a running tab: totals track the *current* catalog price.
    // Issued orders are frozen and read their add-time snapshots forever.

    [HumansFact]
    public void Open_order_reprices_to_current_price_when_lower()
    {
        var productId = Guid.NewGuid();
        var order = OpenOrderWithLine(productId, snapshotUnit: 50m, snapshotVat: 21m);
        var current = Prices((productId, 40m, 21m, null));

        var r = BalanceCalculator.Compute(order, current);

        r.LinesSubtotalEur.Should().Be(80m);   // 2 × current 40, not snapshot 50
        r.VatTotalEur.Should().Be(16.80m);
        r.BalanceEur.Should().Be(96.80m);
        r.Lines.Single().EffectiveUnitPrice.Should().Be(40m);
    }

    [HumansFact]
    public void Open_order_reprices_current_vat_and_deposit_when_higher()
    {
        var productId = Guid.NewGuid();
        var order = new StoreOrder
        {
            State = StoreOrderState.Open,
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), ProductId = productId, Qty = 1,
                        UnitPriceSnapshot = 50m, VatRateSnapshot = 21m, DepositAmountSnapshot = 10m }
            }
        };
        var current = Prices((productId, 60m, 10m, 25m));

        var r = BalanceCalculator.Compute(order, current);

        r.LinesSubtotalEur.Should().Be(60m);
        r.VatTotalEur.Should().Be(6m);       // 10% of 60, not snapshot 21%
        r.DepositTotalEur.Should().Be(25m);  // current deposit, not snapshot 10
        r.BalanceEur.Should().Be(91m);
        var line = r.Lines.Single();
        line.EffectiveUnitPrice.Should().Be(60m);
        line.EffectiveVatRate.Should().Be(10m);
        line.EffectiveDeposit.Should().Be(25m);
    }

    [HumansFact]
    public void Issued_order_ignores_current_price_and_uses_snapshot()
    {
        var productId = Guid.NewGuid();
        var order = OpenOrderWithLine(productId, snapshotUnit: 50m, snapshotVat: 21m);
        order.State = StoreOrderState.InvoiceIssued;
        var current = Prices((productId, 40m, 21m, null));

        var r = BalanceCalculator.Compute(order, current);

        r.LinesSubtotalEur.Should().Be(100m); // 2 × snapshot 50; current 40 ignored
        r.Lines.Single().EffectiveUnitPrice.Should().Be(50m);
    }

    [HumansFact]
    public void Open_order_falls_back_to_snapshot_when_current_price_unresolved()
    {
        var order = OpenOrderWithLine(Guid.NewGuid(), snapshotUnit: 50m, snapshotVat: 21m);
        var current = Prices(); // no entry for this product

        var r = BalanceCalculator.Compute(order, current);

        r.LinesSubtotalEur.Should().Be(100m); // 2 × snapshot 50
        r.Lines.Single().EffectiveUnitPrice.Should().Be(50m);
    }

    [HumansFact]
    public void Open_order_balance_goes_negative_when_repriced_below_payments()
    {
        var productId = Guid.NewGuid();
        var order = new StoreOrder
        {
            State = StoreOrderState.Open,
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), ProductId = productId, Qty = 1, UnitPriceSnapshot = 100m, VatRateSnapshot = 0m }
            },
            Payments = new List<StorePayment>
            {
                new() { AmountEur = 100m, Method = StorePaymentMethod.Stripe }
            }
        };
        var current = Prices((productId, 60m, 0m, null));

        var r = BalanceCalculator.Compute(order, current);

        r.BalanceEur.Should().Be(-40m); // 60 due − 100 paid: credit owed
    }

    private static StoreOrder OpenOrderWithLine(Guid productId, decimal snapshotUnit, decimal snapshotVat) =>
        new()
        {
            State = StoreOrderState.Open,
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), ProductId = productId, Qty = 2,
                        UnitPriceSnapshot = snapshotUnit, VatRateSnapshot = snapshotVat }
            }
        };

    private static IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice> Prices(
        params (Guid Id, decimal Unit, decimal Vat, decimal? Deposit)[] entries) =>
        entries.ToDictionary(e => e.Id, e => new BalanceCalculator.ProductPrice(e.Unit, e.Vat, e.Deposit));
}
