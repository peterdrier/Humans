namespace Humans.Store.Contracts;

internal sealed record OrderSummaryDto(
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    OrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
