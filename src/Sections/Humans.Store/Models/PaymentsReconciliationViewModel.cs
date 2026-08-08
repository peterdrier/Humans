using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

/// <summary>
/// View model for the Store → Stripe Payments admin screen. Wraps the reconciliation
/// report with the display-ordered rows the controller prepared.
/// </summary>
internal sealed class PaymentsReconciliationViewModel
{
    public required StripeReconciliationReport Report { get; init; }

    /// <summary>Rows ordered for display: problems (Missing / Unmatched) first, then newest.</summary>
    public required IReadOnlyList<StripeReconciliationRow> Rows { get; init; }
}
