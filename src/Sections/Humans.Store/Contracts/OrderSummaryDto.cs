namespace Humans.Store.Contracts;

public sealed record OrderSummaryDto(
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    string? Label,
    OrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
