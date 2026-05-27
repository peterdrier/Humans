using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public record OrderSummaryDto(
    Guid OrderId,
    StoreOrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    string? Label,
    StoreOrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
