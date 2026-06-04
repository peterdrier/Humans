using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models.Store;

/// <summary>
/// View model for the Store → Stripe Payments admin screen. Wraps the reconciliation
/// report with the display-ordered rows the controller prepared.
/// </summary>
public sealed class StorePaymentsReconciliationViewModel
{
    public required StripeReconciliationReport Report { get; init; }

    /// <summary>Rows ordered for display: problems (Missing / Unmatched) first, then newest.</summary>
    public required IReadOnlyList<StripeReconciliationRow> Rows { get; init; }
}
