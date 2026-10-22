using Humans.Expenses.Contracts;
using NodaTime;

namespace Humans.Expenses.Domain;

internal sealed class ExpenseReport
{
    public Guid Id { get; init; }
    public Guid SubmitterUserId { get; set; }
    /// <summary>0 only on rows created before accounts were chosen on the report; those book via the category map.</summary>
    public int HoldedAccountNumber { get; set; }
    /// <summary>Captured when the account was chosen.</summary>
    public string? HoldedAccountId { get; set; }
    /// <summary>Null when the account is not a budget category.</summary>
    public Guid? BudgetCategoryId { get; set; }
    public Guid? BudgetYearId { get; set; }
    /// <summary>Set at draft; the account and cap are restamped from the grant at submit.</summary>
    public Guid? PreapprovalGrantId { get; set; }
    public ExpenseReportStatus Status { get; set; }
    public string? Note { get; set; }
    public string PayeeName { get; set; } = "";
    public string PayeeIban { get; set; } = "";
    /// <summary>Top-level lines not declined; the repository keeps it in step.</summary>
    public decimal Total { get; set; }
    /// <summary>Null for no cap. Payable is min(Total, MaxAmount).</summary>
    public decimal? MaxAmount { get; set; }
    public Instant? SubmittedAt { get; set; }
    public Guid? CoordinatorEndorsedByUserId { get; set; }
    public Instant? CoordinatorEndorsedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Instant? ApprovedAt { get; set; }
    public string? LastRejectionReason { get; set; }
    public Guid? LastRejectedByUserId { get; set; }
    public Instant? LastRejectedAt { get; set; }
    /// <summary>Legacy single-doc pushes only; never set again. New pushes write
    /// <see cref="ExpenseLine.HoldedDocId"/>.</summary>
    public string? HoldedDocId { get; set; }
    /// <summary>Set on first push.</summary>
    public string? HoldedContactId { get; set; }
    /// <summary>supplierRecord.num, cached at push time.</summary>
    public int? HoldedSupplierAccountNum { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<ExpenseLine> Lines { get; set; } = new List<ExpenseLine>();
}
