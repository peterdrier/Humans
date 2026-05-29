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
    public decimal UnitPriceSnapshotInclVat => Math.Round(UnitPriceSnapshot * (1 + VatRateSnapshot / 100m), 2);
}
