using NodaTime;

namespace Humans.Domain.Entities;

public class CampImage
{
    public Guid Id { get; init; }

    public Guid CampId { get; init; }
    public Camp Camp { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public Instant UploadedAt { get; init; }
}
