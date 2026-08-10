using Humans.Domain.Enums;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Services.Dtos;

internal sealed record ExpenseLineDto
{
    public required Guid Id { get; init; }
    public required Guid ExpenseReportId { get; init; }
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public required ExpenseLineType LineType { get; init; }
    public Guid? AttachmentId { get; init; }
    public ExpenseAttachmentDto? Attachment { get; init; }
    public required int SortOrder { get; init; }
}
