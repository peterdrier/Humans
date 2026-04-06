using NodaTime;

namespace Humans.Domain.Entities;

public class CampPolygon
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public string GeoJson { get; set; } = string.Empty;
    public double AreaSqm { get; set; }

    public Guid LastModifiedByUserId { get; set; }
    public User LastModifiedByUser { get; set; } = null!;

    public Instant LastModifiedAt { get; set; }
}
