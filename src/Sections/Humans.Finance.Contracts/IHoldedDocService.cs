using Humans.Application.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>
/// What other sections may ask of Finance's purchase-document pipeline. Provisioning and the
/// unmatched queue are admin operations and are deliberately absent — they live on the section's
/// internal <c>IHoldedDocAdminService</c>, which only <c>FinanceController</c> sees.
/// </summary>
public interface IHoldedDocService : IApplicationService
{
    /// <summary>Pulls every purchase doc from Holded, re-attributes it, and upserts the mirror.</summary>
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>State of the last purchase-doc sync. Read-only; lazy-creates the state row.</summary>
    Task<HoldedDocSyncInfo> GetDocSyncInfoAsync(CancellationToken ct = default);

    /// <summary>Approved, matched doc totals for the calendar year, grouped by budget category.</summary>
    Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(int calendarYear, CancellationToken ct = default);

    /// <summary>The active Holded expense-account id mapped to this budget category
    /// (holded_category_map), or null when no active mapping exists. Used to book a purchase
    /// line's `items[].account` directly at doc creation.</summary>
    Task<string?> GetHoldedAccountIdForCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);
}
