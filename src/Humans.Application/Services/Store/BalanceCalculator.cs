using Humans.Domain.Entities;

namespace Humans.Application.Services.Store;

public static class BalanceCalculator
{
    public record Result(
        decimal LinesSubtotalEur,
        decimal VatTotalEur,
        decimal DepositTotalEur,
        decimal PaymentsTotalEur,
        decimal BalanceEur);

    public static Result Compute(StoreOrder order)
    {
        decimal subtotal = 0m;
        decimal vat = 0m;
        decimal deposits = 0m;

        foreach (var line in order.Lines)
        {
            var lineSubtotal = line.Qty * line.UnitPriceSnapshot;
            subtotal += lineSubtotal;
            vat += Math.Round(lineSubtotal * line.VatRateSnapshot / 100m, 2, MidpointRounding.AwayFromZero);
            if (line.DepositAmountSnapshot is { } deposit)
                deposits += line.Qty * deposit;
        }

        var payments = order.Payments.Sum(p => p.AmountEur);
        var balance = subtotal + vat + deposits - payments;

        return new Result(subtotal, vat, deposits, payments, balance);
    }
}
