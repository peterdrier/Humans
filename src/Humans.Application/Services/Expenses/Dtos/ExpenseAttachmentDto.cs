using NodaTime;

namespace Humans.Application.Services.Expenses.Dtos;

public sealed record ExpenseAttachmentDto
{
    public required Guid Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string Extension { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required Guid UploadedByUserId { get; init; }
    public required Instant UploadedAt { get; init; }
}
