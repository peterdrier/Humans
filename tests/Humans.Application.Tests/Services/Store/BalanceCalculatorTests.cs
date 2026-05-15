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
            new BalanceCalculator.LineTotals(lineId, 100m, 21m, 0m, 121m));
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
            new BalanceCalculator.LineTotals(lineId, 30m, 6.30m, 100m, 136.30m));
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
            new BalanceCalculator.LineTotals(lineId, 100m, 21m, 0m, 121m));
    }
}
