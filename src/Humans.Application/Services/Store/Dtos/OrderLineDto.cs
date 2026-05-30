using NodaTime;

namespace Humans.Application.Services.Store.Dtos;

public record OrderLineDto(
    Guid Id,
    Guid OrderId,
    Guid ProductId,
    string ProductName,
    int Qty,
    decimal UnitPriceSnapshot,
    decimal VatRateSnapshot,
    decimal? DepositAmountSnapshot,
    Instant AddedAt,
    decimal SubtotalEur,
    decimal VatEur,
    decimal DepositEur,
    decimal TotalEur)
{
    /// <summary>
    /// Per-unit price including VAT, for display only. Rounded to 2 dp away-from-zero to match the
    /// authoritative VAT rounding used by BalanceCalculator. Note: for <c>Qty &gt; 1</c>,
    /// <c>Qty * UnitPriceSnapshotInclVat</c> is not guaranteed to equal <c>TotalEur</c>, because the
    /// authoritative line VAT is rounded once over the whole line subtotal rather than per unit.
    /// </summary>
    public decimal UnitPriceSnapshotInclVat => Math.Round(UnitPriceSnapshot * (1 + VatRateSnapshot / 100m), 2, MidpointRounding.AwayFromZero);
}
