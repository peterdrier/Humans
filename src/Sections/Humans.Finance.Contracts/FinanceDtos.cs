using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary><c>Docs</c> are the approved purchase docs the actual sums; the year page lists them so a wrong total
/// can be traced to its document.</summary>
public sealed record CategoryActualRow(
    Guid BudgetCategoryId, decimal Actual, IReadOnlyList<CategoryActualDoc> Docs);

public sealed record CategoryActualDoc(
    string HoldedDocId, string DocNumber, string ContactName, string? Description, LocalDate Date,
    decimal Total, string HoldedUrl);

public sealed record UnmatchedDocRow(
    string HoldedDocId, string DocNumber, string ContactName, string? Description, decimal Total,
    string Reason, string HoldedUrl)
{
    public LocalDate Date { get; init; }
}


/// <summary><c>Created</c> is true when this call created the account.</summary>
public sealed record ExpenseAccountRef(int AccountNum, string AccountId, string Name, bool Created);

/// <summary>A budget category's account (labelled "Group / Category" from the active budget year; inactive when the
/// category is not in that year) or a Finance-managed account (inactive when retired).</summary>
public sealed record ExpenseAccountOption(
    int AccountNum, string AccountId, string Label, bool IsBudgetCategory, bool IsActive);

/// <summary>What a budget category is actually booked to today, as opposed to the plan
/// <c>/Finance/HoldedAccounts</c> renders.</summary>
/// <remarks>
/// <paramref name="CategoryName"/> is null when the category is not in the active budget year (deleted, or an earlier
/// year); the provisioning page calls that an Orphan.
/// </remarks>
public sealed record CategoryAccountRow(
    Guid BudgetCategoryId,
    string? CategoryName,
    string? GroupName,
    int HoldedAccountNumber,
    string HoldedAccountId,
    string Tag,
    Instant UpdatedAt);
