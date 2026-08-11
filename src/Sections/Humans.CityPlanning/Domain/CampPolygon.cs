using NodaTime;

namespace Humans.CityPlanning.Domain;

internal sealed class CampPolygon
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CampSeasonId { get; init; }

    public string GeoJson { get; set; } = string.Empty;
    public double AreaSqm { get; set; }

    public Guid LastModifiedByUserId { get; set; }

    public Instant LastModifiedAt { get; set; }
}
