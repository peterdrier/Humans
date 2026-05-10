using NodaTime;

namespace Humans.Domain.Entities;

public class ContainerPlacement
{
    public Guid ContainerId { get; init; }
    public int Year { get; init; }

    public string? LocationGeoJson { get; set; }
    public string? PlacementNotes { get; set; }
    public string? PlacementImageStoragePath { get; set; }
    public string? PlacementImageContentType { get; set; }
    public string? PlacementImageFileName { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
