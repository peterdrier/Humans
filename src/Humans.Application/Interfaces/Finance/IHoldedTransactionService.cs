using Humans.Application.DTOs.Finance;

namespace Humans.Application.Interfaces.Finance;

/// <summary>
/// Read-side queries against the synced Holded purchase docs. Wraps
/// <see cref="Humans.Application.Interfaces.Repositories.IHoldedRepository"/>
/// and projects entities into view-friendly DTOs (with plain-language match
/// status and deep-link URLs).
/// </summary>
public interface IHoldedTransactionService
{
    /// <summary>All docs whose <c>MatchStatus</c> is anything other than Matched.</summary>
    Task<IReadOnlyList<HoldedTransactionDto>> GetUnmatchedAsync(CancellationToken ct = default);

    /// <summary>All docs assigned to the given budget category.</summary>
    Task<IReadOnlyList<HoldedTransactionDto>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);

    /// <summary>Sum of <c>Total</c> per BudgetCategoryId for docs dated within the given budget year.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default);

    /// <summary>Count of docs needing manual reassignment (anything not Matched).</summary>
    Task<int> CountUnmatchedAsync(CancellationToken ct = default);
}
