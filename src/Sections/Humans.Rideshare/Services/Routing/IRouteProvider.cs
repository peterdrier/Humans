using System.Runtime.InteropServices;

namespace Humans.Rideshare.Services.Routing;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// Geocoding + directions behind one seam so the provider can be swapped. Both calls
/// are best-effort: a <c>null</c> means "not found / provider unavailable", never an
/// exception. Only coarse, city-level places are ever sent out.
/// </summary>
internal interface IRouteProvider
{
    /// <summary>Best match for a free-text place, or <c>null</c> when nothing matched or the provider is unavailable.</summary>
    Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Driving route through <paramref name="points"/> in order, as a GeoJSON geometry
    /// (LineString) JSON string; <c>null</c> when the provider is unavailable.
    /// </summary>
    Task<string?> GetRouteGeoJsonAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default);
}
