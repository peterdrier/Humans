using Humans.Expenses.Contracts;

namespace Humans.Expenses.Domain;

internal sealed class ExpenseLine
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public ExpenseLineType LineType { get; set; }
    public Guid? AttachmentId { get; set; }
    /// <summary>Non-null marks a proof row backing the referenced Invoice line. Proof rows are
    /// excluded from the report total and from the Holded push — reviewed, never booked.</summary>
    public Guid? ParentLineId { get; set; }
    public int SortOrder { get; set; }
    /// <summary>The Holded purchase document this line booked to — each bookable line gets its own
    /// doc, carrying its one receipt. Null until pushed, and always null on proof rows and on lines
    /// the authorized cap zeroed out. Reports pushed before per-line docs carry the single doc id
    /// on <see cref="ExpenseReport.HoldedDocId"/> instead.</summary>
    public string? HoldedDocId { get; set; }

    public ExpenseAttachment? Attachment { get; set; }
}
