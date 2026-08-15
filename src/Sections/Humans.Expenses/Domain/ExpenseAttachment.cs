using NodaTime;

namespace Humans.Expenses.Domain;

internal sealed class ExpenseAttachment
{
    public Guid Id { get; init; }
    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
    public Instant UploadedAt { get; init; }
    /// <summary>
    /// When this file was pushed to the report's Holded purchase document. Null = not yet
    /// uploaded. Makes the outbox push resumable: a retry after a partial failure skips the
    /// files Holded already has instead of duplicating them on the existing doc.
    /// </summary>
    public Instant? HoldedUploadedAt { get; set; }
}
