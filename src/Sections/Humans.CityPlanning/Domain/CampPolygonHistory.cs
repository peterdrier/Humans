using NodaTime;

namespace Humans.CityPlanning.Domain;

internal sealed class CampPolygonHistory
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CampSeasonId { get; init; }

    public string GeoJson { get; init; } = string.Empty;
    public double AreaSqm { get; init; }

    public Guid ModifiedByUserId { get; init; }

    public Instant ModifiedAt { get; init; }

    /// <summary>
    /// "Saved" by default; "Restored from {timestamp} UTC" for restores; "Imported
    /// {timestamp}" for the admin bulk import, which sets it client-side. Not a closed set —
    /// the API takes whatever note the caller sends, up to 512 characters.
    /// </summary>
    public string Note { get; init; } = "Saved";
}
