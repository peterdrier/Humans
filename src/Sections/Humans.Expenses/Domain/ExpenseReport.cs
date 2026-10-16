using Humans.Expenses.Contracts;
using NodaTime;

namespace Humans.Expenses.Domain;

internal sealed class ExpenseReport
{
    public Guid Id { get; init; }
    public Guid SubmitterUserId { get; set; }
    /// <summary>Holded expense account (accounting number) this report books to. 0 only on rows
    /// created before accounts were chosen on the report; those still book via the category map.</summary>
    public int HoldedAccountNumber { get; set; }
    /// <summary>Holded id of <see cref="HoldedAccountNumber"/>, captured when the account was chosen.</summary>
    public string? HoldedAccountId { get; set; }
    /// <summary>Budget category the account maps to; null when the account is not a budget category.</summary>
    public Guid? BudgetCategoryId { get; set; }
    /// <summary>Budget year of <see cref="BudgetCategoryId"/>; null when the report has no category.</summary>
    public Guid? BudgetYearId { get; set; }
    public ExpenseReportStatus Status { get; set; }
    public string? Note { get; set; }
    public string PayeeName { get; set; } = "";
    public string PayeeIban { get; set; } = "";
    public decimal Total { get; set; }
    /// <summary>Cap the deciders authorized, or null for no cap. Payable is min(Total, MaxAmount).</summary>
    public decimal? MaxAmount { get; set; }
    public Instant? SubmittedAt { get; set; }
    public Guid? CoordinatorEndorsedByUserId { get; set; }
    public Instant? CoordinatorEndorsedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Instant? ApprovedAt { get; set; }
    public string? LastRejectionReason { get; set; }
    public Guid? LastRejectedByUserId { get; set; }
    public Instant? LastRejectedAt { get; set; }
    /// <summary>Legacy single-doc pushes only (pre per-line docs). New pushes create one doc per
    /// bookable line and write <see cref="ExpenseLine.HoldedDocId"/>; this is never set again.</summary>
    public string? HoldedDocId { get; set; }
    /// <summary>Holded contact id for this submitter (set on first push). Links to creditor balance + payments.</summary>
    public string? HoldedContactId { get; set; }
    /// <summary>Resolved 400000xx supplier-account number (supplierRecord.num), cached at push time.</summary>
    public int? HoldedSupplierAccountNum { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<ExpenseLine> Lines { get; set; } = new List<ExpenseLine>();
}
