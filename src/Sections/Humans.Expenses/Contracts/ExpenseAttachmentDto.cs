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
    /// <summary>Null until pushed.</summary>
    public Instant? HoldedUploadedAt { get; init; }

    public bool IsImage => IsImageType(ContentType);

    /// <summary>Browsers render these natively, so they can open inline. Of the allowed upload types that leaves out
    /// only HEIC.</summary>
    public bool IsInlineViewable => IsInlineViewableType(ContentType);

    public static bool IsImageType(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase);

    public static bool IsInlineViewableType(string contentType) =>
        IsImageType(contentType)
        || contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
}
