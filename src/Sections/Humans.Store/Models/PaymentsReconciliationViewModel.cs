using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

internal sealed class PaymentsReconciliationViewModel
{
    public required StripeReconciliationReport Report { get; init; }

    /// <summary>Problems first, then newest.</summary>
    public required IReadOnlyList<StripeReconciliationRow> Rows { get; init; }
}
