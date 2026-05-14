using NodaTime;

namespace Humans.Domain.Entities;

public class ExpenseAttachment
{
    public Guid Id { get; init; }
    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
    public Instant UploadedAt { get; init; }
}
