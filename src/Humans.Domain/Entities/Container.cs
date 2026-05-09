using NodaTime;

namespace Humans.Domain.Entities;

public class Container
{
    public Guid Id { get; init; }

    public Guid? CampId { get; init; }

    public int Year { get; init; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageStoragePath { get; set; }
    public string? ImageContentType { get; set; }
    public string? ImageFileName { get; set; }
    public string? LocationGeoJson { get; set; }
    public string? PlacementNotes { get; set; }
    public string? PlacementImageStoragePath { get; set; }
    public string? PlacementImageContentType { get; set; }
    public string? PlacementImageFileName { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
