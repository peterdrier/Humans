using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>One budget category's Holded actual, plus the approved purchase docs it sums.
/// <c>Docs</c> is what the figure is made of — the year page renders it under the category so a
/// wrong total can be traced to the document that caused it.</summary>
public sealed record HoldedActualRow(
    Guid BudgetCategoryId, decimal Actual, IReadOnlyList<HoldedActualDoc> Docs);

/// <summary>One approved Holded purchase doc behind a category's actual.</summary>
public sealed record HoldedActualDoc(
    string HoldedDocId, string DocNumber, string ContactName, string? Description, LocalDate Date,
    decimal Total, string HoldedUrl);

public sealed record HoldedUnmatchedRow(
    string HoldedDocId, string DocNumber, string ContactName, string? Description, decimal Total,
    string Reason, string HoldedUrl);

public sealed record HoldedSyncResult(int DocCount, int Matched, int Unmatched);

/// <summary>State of Finance's purchase-doc sync, for the /Holded screen's sync table — the
/// one Holded-owned page that shows both syncs. <c>Status</c> is "Idle" | "Running" | "Error".</summary>
public sealed record HoldedDocSyncInfo(
    Instant? LastSyncAt, string Status, string? LastError, int LastSyncedDocCount,
    int CreditorBindingCount);

/// <summary>The outcome of <see cref="IHoldedFinanceService.CreateOrLinkExpenseAccountAsync"/>:
/// the account the caller should book to, and whether this call created it.</summary>
public sealed record HoldedExpenseAccountRef(int AccountNum, string AccountId, string Name, bool Created);

/// <summary>One expense account a caller may book to: a budget category's account (labelled
/// "Group / Category" from the active budget year) or a Finance-managed account (its label).
/// Cache reads only — never a Holded call.</summary>
public sealed record HoldedExpenseAccountOption(
    int AccountNum, string AccountId, string Label, bool IsBudgetCategory, bool IsActive);

/// <summary>One live <c>holded_category_map</c> row — what a budget category is actually booked to
/// today, as opposed to the plan <c>/Finance/HoldedAccounts</c> renders.</summary>
/// <param name="CategoryName">Null when the category is not in the active budget year — a row whose
/// category was deleted or belongs to an earlier year. The provisioning page calls that an Orphan.</param>
/// <param name="BudgetCategoryId">The budget category associated with this mapping.</param>
/// <param name="GroupName">The budget group name, or null when unavailable.</param>
/// <param name="HoldedAccountNumber">The Holded account number used for booking.</param>
/// <param name="HoldedAccountId">The Holded identifier for the mapped account.</param>
/// <param name="Tag">The tag sent to Holded for this category mapping.</param>
/// <param name="IsActive">Whether this mapping is currently active.</param>
/// <param name="UpdatedAt">When this mapping was last updated.</param>
public sealed record HoldedCategoryMapRow(
    Guid BudgetCategoryId,
    string? CategoryName,
    string? GroupName,
    int HoldedAccountNumber,
    string HoldedAccountId,
    string Tag,
    bool IsActive,
    Instant UpdatedAt);
