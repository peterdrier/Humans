using NodaTime;

namespace Humans.Containers.Domain;

internal sealed class Container
{
    public Guid Id { get; init; }

    public Guid CampId { get; init; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageStoragePath { get; set; }
    public string? ImageContentType { get; set; }
    public string? ImageFileName { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
