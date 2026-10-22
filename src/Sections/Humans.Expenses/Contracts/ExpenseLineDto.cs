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
    /// <summary>Non-null marks a proof row backing that Invoice line: reviewed, never totalled or booked.</summary>
    public Guid? ParentLineId { get; init; }
    public required int SortOrder { get; init; }
    /// <summary>One doc per bookable line. Null until pushed, on proof rows, and on lines the cap zeroed out. Reports
    /// pushed before per-line docs carry their doc id on the report instead.</summary>
    public string? HoldedDocId { get; init; }
    /// <summary>Kept for the record, but out of <c>Total</c> and <c>Payable</c> and never pushed to Holded.</summary>
    public bool Declined { get; init; }
    public string? DeclinedReason { get; init; }
    public Guid? DeclinedByUserId { get; init; }
    public NodaTime.Instant? DeclinedAt { get; init; }
}
