namespace Humans.Expenses.Contracts;

public sealed record ExpenseLineDto
{
    public required Guid Id { get; init; }
    public required Guid ExpenseReportId { get; init; }
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public required ExpenseLineType LineType { get; init; }
    public Guid? AttachmentId { get; init; }
    public ExpenseAttachmentDto? Attachment { get; init; }
    /// <summary>Non-null marks a proof row backing the referenced Invoice line. Proof rows are
    /// excluded from the report total and from the Holded push — reviewed, never booked.</summary>
    public Guid? ParentLineId { get; init; }
    public required int SortOrder { get; init; }
    /// <summary>The Holded purchase document this line booked to (one doc per bookable line).
    /// Null until pushed, on proof rows, and on lines the authorized cap zeroed out. Reports
    /// pushed before per-line docs carry their single doc id on the report instead.</summary>
    public string? HoldedDocId { get; init; }
}
