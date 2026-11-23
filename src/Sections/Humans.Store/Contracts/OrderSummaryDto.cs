namespace Humans.Store.Contracts;

internal sealed record OrderSummaryDto(
    Guid OrderId,
    OrderCounterpartyType CounterpartyType,
    string CounterpartyName,
    OrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
