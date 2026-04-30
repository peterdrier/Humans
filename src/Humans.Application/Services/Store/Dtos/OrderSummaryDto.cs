using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public record OrderSummaryDto(
    Guid OrderId,
    Guid CampSeasonId,
    string CampName,
    string? Label,
    StoreOrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
