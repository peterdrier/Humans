using Humans.Domain.Enums;
using Humans.Store.Domain;

namespace Humans.Store.Services.Dtos;

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
