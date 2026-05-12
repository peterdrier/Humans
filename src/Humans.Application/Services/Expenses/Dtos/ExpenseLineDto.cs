namespace Humans.Application.Services.Expenses.Dtos;

public sealed record ExpenseLineDto
{
    public required Guid Id { get; init; }
    public required Guid ExpenseReportId { get; init; }
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public Guid? AttachmentId { get; init; }
    public ExpenseAttachmentDto? Attachment { get; init; }
    public required int SortOrder { get; init; }
}
