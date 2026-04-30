using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public record OrderDto(
    Guid Id,
    Guid CampSeasonId,
    string? Label,
    StoreOrderState State,
    string? CounterpartyName,
    string? CounterpartyVatId,
    string? CounterpartyAddress,
    string? CounterpartyCountryCode,
    string? CounterpartyEmail,
    Guid? IssuedInvoiceId,
    IReadOnlyList<OrderLineDto> Lines,
    decimal LinesSubtotalEur,
    decimal VatTotalEur,
    decimal DepositTotalEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
