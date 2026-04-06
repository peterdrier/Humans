using NodaTime;

namespace Humans.Domain.Entities;

public class CampPolygonHistory
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public string GeoJson { get; init; } = string.Empty;
    public double AreaSqm { get; init; }

    public Guid ModifiedByUserId { get; init; }
    public User ModifiedByUser { get; set; } = null!;

    public Instant ModifiedAt { get; init; }

    /// <summary>"Saved" by default; "Restored from 2026-03-10T14:32:05Z" for restores.</summary>
    public string Note { get; init; } = "Saved";
}
