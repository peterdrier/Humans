using Humans.Expenses.Contracts;
using NodaTime;

namespace Humans.Expenses.Domain;

internal sealed class ExpenseLine
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public ExpenseLineType LineType { get; set; }
    public Guid? AttachmentId { get; set; }
    /// <summary>Non-null marks a proof row backing that Invoice line: reviewed, never totalled or booked.</summary>
    public Guid? ParentLineId { get; set; }
    public int SortOrder { get; set; }
    /// <summary>One doc per bookable line. Null until pushed, and always null on proof rows and lines the cap zeroed
    /// out. Reports pushed before per-line docs use <see cref="ExpenseReport.HoldedDocId"/> instead.</summary>
    public string? HoldedDocId { get; set; }

    /// <summary>Out of <see cref="ExpenseReport.Total"/> and never pushed. Cleared by an undo or when the submitter
    /// replaces the file. Never set on proof rows.</summary>
    public bool Declined => DeclinedAt is not null;
    public string? DeclinedReason { get; set; }
    public Guid? DeclinedByUserId { get; set; }
    public Instant? DeclinedAt { get; set; }

    public ExpenseAttachment? Attachment { get; set; }
}
