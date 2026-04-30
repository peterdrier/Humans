using Humans.Domain.Entities;

namespace Humans.Application.Services.Store;

public static class BalanceCalculator
{
    public record LineTotals(
        Guid LineId,
        decimal SubtotalEur,
        decimal VatEur,
        decimal DepositEur,
        decimal TotalEur);

    public record Result(
        decimal LinesSubtotalEur,
        decimal VatTotalEur,
        decimal DepositTotalEur,
        decimal PaymentsTotalEur,
        decimal BalanceEur,
        IReadOnlyList<LineTotals> Lines);

    public static Result Compute(StoreOrder order)
    {
        decimal subtotal = 0m;
        decimal vat = 0m;
        decimal deposits = 0m;
        var lineTotals = new List<LineTotals>(order.Lines.Count);

        foreach (var line in order.Lines)
        {
            var lineSubtotal = line.Qty * line.UnitPriceSnapshot;
            var lineVat = Math.Round(lineSubtotal * line.VatRateSnapshot / 100m, 2, MidpointRounding.AwayFromZero);
            var lineDeposit = line.DepositAmountSnapshot is { } deposit ? line.Qty * deposit : 0m;
            var lineTotal = lineSubtotal + lineVat + lineDeposit;

            subtotal += lineSubtotal;
            vat += lineVat;
            deposits += lineDeposit;
            lineTotals.Add(new LineTotals(line.Id, lineSubtotal, lineVat, lineDeposit, lineTotal));
        }

        var payments = order.Payments.Sum(p => p.AmountEur);
        var balance = subtotal + vat + deposits - payments;

        return new Result(subtotal, vat, deposits, payments, balance, lineTotals);
    }
}
