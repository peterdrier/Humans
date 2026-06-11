using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store;

public static class BalanceCalculator
{
    /// <summary>
    /// Current catalog price components for a product, used to reprice the lines
    /// of an <see cref="StoreOrderState.Open"/> order. See nobodies-collective/Humans#816.
    /// </summary>
    public readonly record struct ProductPrice(decimal UnitPriceEur, decimal VatRatePercent, decimal? DepositAmountEur);

    public record LineTotals(
        Guid LineId,
        decimal EffectiveUnitPrice,
        decimal EffectiveVatRate,
        decimal? EffectiveDeposit,
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

    private static readonly IReadOnlyDictionary<Guid, ProductPrice> NoCurrentPrices =
        new Dictionary<Guid, ProductPrice>();

    /// <summary>
    /// Computes order totals from each line's add-time snapshot. Equivalent to
    /// <see cref="Compute(StoreOrder, IReadOnlyDictionary{Guid, ProductPrice})"/>
    /// with no resolvable current prices — every line falls back to its snapshot.
    /// </summary>
    public static Result Compute(StoreOrder order) => Compute(order, NoCurrentPrices);

    /// <summary>
    /// Computes order totals using <em>effective</em> prices: an
    /// <see cref="StoreOrderState.Open"/> order tracks the current catalog price
    /// from <paramref name="currentPrices"/> (a running tab), while an
    /// <see cref="StoreOrderState.InvoiceIssued"/> order is frozen and always
    /// reads each line's add-time snapshot. An Open line whose product is absent
    /// from <paramref name="currentPrices"/> falls back to its snapshot.
    /// See nobodies-collective/Humans#816.
    /// </summary>
    public static Result Compute(StoreOrder order, IReadOnlyDictionary<Guid, ProductPrice> currentPrices)
    {
        var reprice = order.State == StoreOrderState.Open;
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

        // Only settled money counts. Pending (mandate captured, not yet cleared) and Failed
        // (mandate rejected / settlement bounced) are excluded so the balance reflects what Stripe
        // has actually confirmed, never what the donor intended. See nobodies-collective/Humans#638.
        var payments = order.Payments
            .Where(p => p.Status == StorePaymentStatus.Paid)
            .Sum(p => p.AmountEur);
        var balance = subtotal + vat + deposits - payments;

        return new Result(subtotal, vat, deposits, payments, balance, lineTotals);
    }
}
