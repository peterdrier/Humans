using NodaTime;

namespace Humans.Application.DTOs.Finance;

/// <summary>
/// Read-side projection of a Holded purchase doc for views (unmatched queue,
/// per-category drill-down). MatchStatusReason is a plain-language rendering
/// of the underlying <see cref="Humans.Domain.Enums.HoldedMatchStatus"/>.
/// </summary>
public sealed record HoldedTransactionDto(
    string HoldedDocId,
    string HoldedDocNumber,
    string ContactName,
    LocalDate Date,
    decimal Total,
    decimal PaymentsTotal,
    decimal PaymentsPending,
    string Currency,
    bool Approved,
    IReadOnlyList<string> Tags,
    string MatchStatusReason,
    string? HoldedDeepLinkUrl,
    Guid? BudgetCategoryId);

/// <summary>
/// Aggregated actuals for a single budget category over the configured budget year.
/// Returned alongside the per-category dictionary in summary views.
/// </summary>
public sealed record CategoryActualDto(Guid BudgetCategoryId, decimal ActualTotal);
