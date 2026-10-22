using Humans.Store.Contracts;
using Humans.Store.Domain;

namespace Humans.Store.Services;

internal static class BalanceCalculator
{
    /// <summary>Current catalog price components, used to reprice an Open order's lines.</summary>
    internal readonly record struct ProductPrice(decimal UnitPriceEur, decimal VatRatePercent, decimal? DepositAmountEur);

    internal sealed record LineTotals(
        Guid LineId,
        decimal EffectiveUnitPrice,
        decimal EffectiveVatRate,
        decimal? EffectiveDeposit,
        decimal SubtotalEur,
        decimal VatEur,
        decimal DepositEur,
        decimal TotalEur);

    internal sealed record Result(
        decimal LinesSubtotalEur,
        decimal VatTotalEur,
        decimal DepositTotalEur,
        decimal PaymentsTotalEur,
        decimal BalanceEur,
        IReadOnlyList<LineTotals> Lines)
    {
        /// <summary>What the order costs: lines, VAT and deposits, before any payment.</summary>
        public decimal TotalDueEur => LinesSubtotalEur + VatTotalEur + DepositTotalEur;
    }

    private static readonly IReadOnlyDictionary<Guid, ProductPrice> NoCurrentPrices =
        new Dictionary<Guid, ProductPrice>();

    /// <summary>Every line priced at its add-time snapshot.</summary>
    public static Result Compute(Order order) => Compute(order, NoCurrentPrices);

    /// <summary>
    /// An Open order is a running tab at the current catalog price from <paramref name="currentPrices"/>, falling back
    /// to the snapshot for an absent product; an InvoiceIssued order is frozen at its snapshots.
    /// </summary>
    public static Result Compute(Order order, IReadOnlyDictionary<Guid, ProductPrice> currentPrices)
    {
        var reprice = order.State == OrderState.Open;
        decimal subtotal = 0m;
        decimal vat = 0m;
        decimal deposits = 0m;
        var lineTotals = new List<LineTotals>(order.Lines.Count);

        foreach (var line in order.Lines)
        {
            var (unitPrice, vatRate, depositPerUnit) = reprice && currentPrices.TryGetValue(line.ProductId, out var current)
                ? (current.UnitPriceEur, current.VatRatePercent, current.DepositAmountEur)
                : (line.UnitPriceSnapshot, line.VatRateSnapshot, line.DepositAmountSnapshot);

            var lineSubtotal = line.Qty * unitPrice;
            var lineVat = Math.Round(lineSubtotal * vatRate / 100m, 2, MidpointRounding.AwayFromZero);
            var lineDeposit = depositPerUnit is { } deposit ? line.Qty * deposit : 0m;
            var lineTotal = lineSubtotal + lineVat + lineDeposit;

            subtotal += lineSubtotal;
            vat += lineVat;
            deposits += lineDeposit;
            lineTotals.Add(new LineTotals(line.Id, unitPrice, vatRate, depositPerUnit, lineSubtotal, lineVat, lineDeposit, lineTotal));
        }

        // Only settled money counts: Pending and Failed are what the payer intended, not what Stripe confirmed.
        var payments = order.Payments
            .Where(p => p.Status == PaymentStatus.Paid)
            .Sum(p => p.AmountEur);
        var balance = subtotal + vat + deposits - payments;

        return new Result(subtotal, vat, deposits, payments, balance, lineTotals);
    }
}
