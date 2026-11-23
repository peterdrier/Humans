using System.Globalization;
using Humans.Base.Extensions;
using Humans.Store.Domain;

namespace Humans.Store.Services;

/// <summary>
/// What a camp order's invoice will hold, and whether it may be issued. Returned deposits never reach the invoice;
/// a kept deposit is income on its product's revenue account at <see cref="KeptDepositVatRate"/>% VAT inside the
/// amount; a write-off is a document discount, so its VAT splits pro rata over the lines. Overpayments never reach it:
/// they are refunded from Stripe, and the gate waits until they are.
/// </summary>
/// <remarks>
/// Totals are modelled the way Holded computes them: each line's net is <c>units × price</c> less the discount, rounded
/// to cents, and its VAT is rounded per line. The gate refuses an order whose kept deposits or write-off can't be hit to
/// the cent that way, since the invoice must equal what the camp paid exactly.
/// </remarks>
internal sealed record StoreInvoicePlan(
    IReadOnlyList<StoreInvoicePlan.Line> Lines,
    decimal DiscountPercent,
    decimal DepositsChargedEur,
    decimal DepositsReturnedEur,
    decimal WriteOffEur,
    decimal SubtotalEur,
    decimal TaxEur,
    decimal TotalEur,
    IReadOnlyList<StoreInvoicePlan.DepositReturn> Returns,
    string? Refusal)
{
    public const decimal KeptDepositVatRate = 21m;

    public decimal KeptDepositsEur => DepositsChargedEur - DepositsReturnedEur;

    /// <summary><paramref name="Price"/> is VAT-exclusive per unit.</summary>
    internal sealed record Line(Guid ProductId, string Name, decimal Units, decimal Price, decimal VatRate);

    internal sealed record DepositReturn(decimal AmountEur, NodaTime.Instant ReturnedAt, string? ProductName);

    /// <summary><paramref name="productNames"/> must name every product on the order.</summary>
    public static StoreInvoicePlan Build(
        Order order, BalanceCalculator.Result totals, IReadOnlyDictionary<Guid, string> productNames)
    {
        var totalsByLine = totals.Lines.ToDictionary(t => t.LineId);
        var lines = new List<Line>();
        foreach (var line in order.Lines)
        {
            var t = totalsByLine[line.Id];
            lines.Add(new Line(line.ProductId, productNames[line.ProductId], line.Qty, t.EffectiveUnitPrice,
                t.EffectiveVatRate));
        }

        var chargedByProduct = order.Lines
            .GroupBy(l => l.ProductId)
            .Select(g => (ProductId: g.Key, Eur: g.Sum(l => totalsByLine[l.Id].DepositEur)))
            .Where(x => x.Eur > 0m)
            .ToList();
        var returns = order.Payments
            .Where(p => p.Method == PaymentMethod.DepositReturn && p.Status == PaymentStatus.Paid)
            .OrderBy(p => p.ReceivedAt)
            .ToList();
        // Returns recorded before they named a product returned every deposit on their order (Peter, 2026-11-19).
        var legacyReturnsCoverAll = returns.Any(r => r.ProductId is null);
        var returnedByProduct = returns.Where(r => r.ProductId is not null)
            .GroupBy(r => r.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AmountEur));

        string? refusal = null;
        foreach (var (productId, charged) in chargedByProduct)
        {
            var kept = legacyReturnsCoverAll ? 0m : charged - returnedByProduct.GetValueOrDefault(productId);
            if (kept < 0m)
            {
                refusal ??= $"More deposit was returned for {productNames[productId]} than was charged "
                    + $"(EUR {charged:0.00} charged, EUR {charged - kept:0.00} returned).";
                continue;
            }
            if (kept == 0m) continue;
            if (SolveNetPrice(kept, KeptDepositVatRate) is { } net)
                lines.Add(new Line(productId, $"{productNames[productId]} – kept deposit", 1m, net, KeptDepositVatRate));
            else
                refusal ??= $"The kept deposit of EUR {kept:0.00} for {productNames[productId]} can't be invoiced to the "
                    + $"cent at {KeptDepositVatRate:0}% VAT.";
        }

        var depositsCharged = chargedByProduct.Sum(x => x.Eur);
        var depositsReturned = legacyReturnsCoverAll ? depositsCharged : returns.Sum(r => r.AmountEur);
        var writeOff = order.Payments
            .Where(p => p.Method == PaymentMethod.WriteOff && p.Status == PaymentStatus.Paid)
            .Sum(p => p.AmountEur);

        var undiscounted = Model(lines, 0m);
        var target = undiscounted.Total - writeOff;
        var discount = 0m;
        if (writeOff != 0m)
        {
            if (SolveDiscount(lines, undiscounted.Total, target) is { } p) discount = p;
            else refusal ??= $"A write-off of EUR {writeOff:0.00} can't be put on the invoice as a discount to the cent.";
        }
        var model = Model(lines, discount);

        refusal ??= GateRefusal(order, totals);

        var returnList = returns.Select(r => new DepositReturn(
            r.AmountEur, r.ReceivedAt, r.ProductId is { } id ? productNames.GetValueOrDefault(id) : null)).ToList();
        return new StoreInvoicePlan(lines, discount, depositsCharged, depositsReturned, writeOff,
            model.Subtotal, model.Tax, model.Total, returnList, refusal);
    }

    /// <summary>No refund pending or owed, and the balance (goods + kept deposits + write-off − payments + refunds) at
    /// exactly zero.</summary>
    private static string? GateRefusal(Order order, BalanceCalculator.Result totals)
    {
        if (order.Payments.Any(p => p.Method == PaymentMethod.Refund && p.Status == PaymentStatus.Pending))
            return "A refund is still pending in Stripe; wait for it to go through before invoicing.";
        if (totals.BalanceEur < 0m)
            return $"The camp is owed a refund of EUR {-totals.BalanceEur:0.00}; refund it in Stripe and record it before invoicing.";
        if (totals.BalanceEur > 0m)
            return $"The camp still owes EUR {totals.BalanceEur:0.00}; it must pay, or the shortfall be written off, before invoicing.";
        return null;
    }

    /// <summary>Holded's arithmetic: per line, net rounded to cents after the discount, then VAT rounded.</summary>
    internal static (decimal Subtotal, decimal Tax, decimal Total) Model(IReadOnlyList<Line> lines, decimal discountPercent)
    {
        decimal subtotal = 0m, tax = 0m;
        foreach (var l in lines)
        {
            var net = Math.Round(l.Units * l.Price * (1m - discountPercent / 100m), 2, MidpointRounding.AwayFromZero);
            subtotal += net;
            tax += Math.Round(net * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero);
        }
        return (subtotal, tax, subtotal + tax);
    }

    /// <summary>The 2-decimal net price whose VAT brings it to exactly <paramref name="gross"/>, or null when none does.</summary>
    internal static decimal? SolveNetPrice(decimal gross, decimal vatRate)
    {
        var guess = Math.Round(gross / (1m + vatRate / 100m), 2, MidpointRounding.AwayFromZero);
        foreach (var step in new[] { 0m, -0.01m, 0.01m, -0.02m, 0.02m })
        {
            var net = guess + step;
            if (net + Math.Round(net * vatRate / 100m, 2, MidpointRounding.AwayFromZero) == gross) return net;
        }
        return null;
    }

    /// <summary>The 4-decimal discount percentage closest to the write-off's share that lands the total on
    /// <paramref name="target"/>, or null when none does.</summary>
    private static decimal? SolveDiscount(IReadOnlyList<Line> lines, decimal undiscountedTotal, decimal target)
    {
        if (undiscountedTotal <= 0m || target < 0m) return null;
        var guess = Math.Round((undiscountedTotal - target) / undiscountedTotal * 100m, 4, MidpointRounding.AwayFromZero);
        for (var k = 0; k <= 200; k++)
        {
            foreach (var p in k == 0 ? new[] { guess } : new[] { guess - k * 0.0001m, guess + k * 0.0001m })
            {
                if (p <= 0m || p > 100m) continue;
                if (Model(lines, p).Total == target) return p;
            }
        }
        return null;
    }

    /// <summary>The camp first, so the printed invoice says who it was for; returned deposits are listed with their
    /// dates, never as lines.</summary>
    public string Notes(string campName, Guid orderId)
    {
        var notes = new List<string> { campName, $"Humans store order {orderId}" };
        foreach (var r in Returns)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Deposit returned: EUR {r.AmountEur:0.00} on {r.ReturnedAt.InUtc().Date.ToInvariantDate()}")
                + (r.ProductName is { } name ? $" ({name})" : string.Empty));
        return string.Join("\n", notes);
    }
}
