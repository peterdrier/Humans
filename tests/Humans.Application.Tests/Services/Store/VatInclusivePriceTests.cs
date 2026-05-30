using System.Globalization;
using AwesomeAssertions;
using Humans.Application.Services.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Xunit;

namespace Humans.Application.Tests.Services.Store;

public class VatInclusivePriceTests
{
    // ex, vatPercent, expectedIncl — passed as strings because decimal is not a valid InlineData literal.
    [HumansTheory]
    [InlineData("9.99", "21", "12.09")]   // standard rate, rounds up from 12.0879
    [InlineData("50", "21", "60.50")]
    [InlineData("10", "10", "11.00")]     // reduced rate
    [InlineData("10", "4", "10.40")]      // super-reduced rate
    [InlineData("12.34", "0", "12.34")]   // 0% VAT is a no-op, must not regress
    [InlineData("0.50", "21", "0.61")]    // 0.605 midpoint: away-from-zero rounds to 0.61 (banker's would give 0.60)
    public void ProductDto_unit_price_incl_vat(string ex, string vat, string expected)
    {
        Product(D(ex), D(vat)).UnitPriceInclVatEur.Should().Be(D(expected));
    }

    [HumansTheory]
    [InlineData("9.99", "21", "12.09")]
    [InlineData("50", "21", "60.50")]
    [InlineData("10", "10", "11.00")]
    [InlineData("10", "4", "10.40")]
    [InlineData("12.34", "0", "12.34")]
    [InlineData("0.50", "21", "0.61")]
    public void OrderLineDto_unit_price_incl_vat(string ex, string vat, string expected)
    {
        Line(qty: 1, unitEx: D(ex), vat: D(vat)).UnitPriceSnapshotInclVat.Should().Be(D(expected));
    }

    [HumansFact]
    public void Per_unit_incl_vat_is_display_only_and_not_additive_for_multi_qty()
    {
        // qty 3 × €9.99 @ 21%: per-unit incl VAT is rounded per unit (€12.09), but the
        // authoritative line VAT is rounded once on the whole subtotal. The two intentionally
        // diverge by a cent for multi-quantity lines; this locks that documented behavior.
        var line = Line(qty: 3, unitEx: 9.99m, vat: 21m);
        line.UnitPriceSnapshotInclVat.Should().Be(12.09m);

        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Qty = 3, UnitPriceSnapshot = 9.99m, VatRateSnapshot = 21m }
            }
        };
        var authoritativeLineTotal = BalanceCalculator.Compute(order).Lines.Single().TotalEur;

        authoritativeLineTotal.Should().Be(36.26m);
        (3 * line.UnitPriceSnapshotInclVat).Should().Be(36.27m);
        (3 * line.UnitPriceSnapshotInclVat).Should().NotBe(authoritativeLineTotal,
            "per-unit incl VAT is display-only; the authoritative line total rounds VAT once on the subtotal");
    }

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static ProductDto Product(decimal unitEx, decimal vat) =>
        new(Guid.NewGuid(), 2026, "P", "", unitEx, vat, null, default, true);

    private static OrderLineDto Line(int qty, decimal unitEx, decimal vat) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "P", qty, unitEx, vat, null,
            default, 0m, 0m, 0m, 0m);
}
