using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Expenses.Dtos;

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
    public Instant? SubmittedAt { get; init; }
    public Guid? CoordinatorEndorsedByUserId { get; init; }
    public Instant? CoordinatorEndorsedAt { get; init; }
    public Guid? ApprovedByUserId { get; init; }
    public Instant? ApprovedAt { get; init; }
    public Instant? SepaSentAt { get; init; }
    public Instant? PaidAt { get; init; }
    public string? LastRejectionReason { get; init; }
    public Guid? LastRejectedByUserId { get; init; }
    public Instant? LastRejectedAt { get; init; }
    public string? HoldedDocId { get; init; }
    public required Instant CreatedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
    public required IReadOnlyList<ExpenseLineDto> Lines { get; init; }
}
