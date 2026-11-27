using NodaTime;

namespace Humans.Store.Services.Dtos;

internal sealed record OrderLineDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    int Qty,
    decimal EffectiveUnitPrice,
    decimal EffectiveVatRate,
    decimal? EffectiveDeposit,
    decimal DepositEur,
    decimal TotalEur)
{
    /// <summary>
    /// Display only, rounded like BalanceCalculator. For <c>Qty &gt; 1</c>, <c>Qty * EffectiveUnitPriceInclVat</c> may
    /// differ from <c>TotalEur</c>: authoritative line VAT is rounded once over the line subtotal, not per unit.
    /// </summary>
    public decimal EffectiveUnitPriceInclVat => Math.Round(EffectiveUnitPrice * (1 + EffectiveVatRate / 100m), 2, MidpointRounding.AwayFromZero);
}
