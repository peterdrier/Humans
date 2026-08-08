using Humans.Store.Domain;

namespace Humans.Store.Services.Dtos;

internal sealed record OrderSummaryDto(
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    string? Label,
    OrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
