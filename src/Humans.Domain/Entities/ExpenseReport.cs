using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class ExpenseReport
{
    public Guid Id { get; init; }
    public Guid SubmitterUserId { get; set; }
    public Guid BudgetCategoryId { get; set; }
    public Guid BudgetYearId { get; set; }
    public ExpenseReportStatus Status { get; set; }
    public string? Note { get; set; }
    public string PayeeName { get; set; } = "";
    public string PayeeIban { get; set; } = "";
    public decimal Total { get; set; }
    public Instant? SubmittedAt { get; set; }
    public Guid? CoordinatorEndorsedByUserId { get; set; }
    public Instant? CoordinatorEndorsedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Instant? ApprovedAt { get; set; }
    public Instant? SepaSentAt { get; set; }
    public Instant? PaidAt { get; set; }
    public string? LastRejectionReason { get; set; }
    public Guid? LastRejectedByUserId { get; set; }
    public Instant? LastRejectedAt { get; set; }
    public string? HoldedDocId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<ExpenseLine> Lines { get; set; } = new List<ExpenseLine>();
}
