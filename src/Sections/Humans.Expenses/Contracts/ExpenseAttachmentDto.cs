using NodaTime;

namespace Humans.Expenses.Contracts;

public sealed record ExpenseAttachmentDto
{
    public required Guid Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string Extension { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required Guid UploadedByUserId { get; init; }
    public required Instant UploadedAt { get; init; }
    /// <summary>When this file reached the report's Holded purchase document; null = not yet pushed.</summary>
    public Instant? HoldedUploadedAt { get; init; }
}
