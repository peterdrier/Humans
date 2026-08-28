using NodaTime;

namespace Humans.Expenses.Contracts;

public sealed record ExpenseReportDto
{
    public required Guid Id { get; init; }
    public required Guid SubmitterUserId { get; init; }
    public required Guid BudgetCategoryId { get; init; }
    public required Guid BudgetYearId { get; init; }
    public required ExpenseReportStatus Status { get; init; }
    public string? Note { get; init; }
    public required string PayeeName { get; init; }
    public required string PayeeIban { get; init; }
    public required decimal Total { get; init; }
    /// <summary>Cap the deciders authorized, or null for no cap.</summary>
    public decimal? MaxAmount { get; init; }
    /// <summary>What is actually reimbursed: the receipts total, capped. The only amount payment math may use.</summary>
    public decimal Payable => MaxAmount is { } cap && cap < Total ? cap : Total;
    public Instant? SubmittedAt { get; init; }
    public Guid? CoordinatorEndorsedByUserId { get; init; }
    public Instant? CoordinatorEndorsedAt { get; init; }
    public Guid? ApprovedByUserId { get; init; }
    public Instant? ApprovedAt { get; init; }
    public string? LastRejectionReason { get; init; }
    public Guid? LastRejectedByUserId { get; init; }
    public Instant? LastRejectedAt { get; init; }
    /// <summary>Legacy single-doc pushes only (pre per-line docs); new pushes set
    /// <see cref="ExpenseLineDto.HoldedDocId"/> per line. Read via <see cref="HoldedDocIds"/>.</summary>
    public string? HoldedDocId { get; init; }
    /// <summary>Every Holded purchase document this report booked to: the legacy report-level doc
    /// when present, else the per-line docs in line order. Empty means not (yet) in Holded.</summary>
    public IReadOnlyList<string> HoldedDocIds => HoldedDocId is not null
        ? [HoldedDocId]
        : Lines.Where(l => l.HoldedDocId is not null)
            .OrderBy(l => l.SortOrder)
            .Select(l => l.HoldedDocId!)
            .ToList();
    public string? HoldedContactId { get; init; }
    public int? HoldedSupplierAccountNum { get; init; }
    public required Instant CreatedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
    public required IReadOnlyList<ExpenseLineDto> Lines { get; init; }
}
