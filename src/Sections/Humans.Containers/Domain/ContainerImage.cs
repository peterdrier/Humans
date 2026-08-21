using NodaTime;

namespace Humans.Containers.Domain;

internal sealed class ContainerImage
{
    public Guid Id { get; init; }

    public Guid ContainerId { get; init; }

    public string StoragePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public Instant CreatedAt { get; init; }
}
