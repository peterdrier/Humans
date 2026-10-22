using System.Runtime.InteropServices;

namespace Humans.Rideshare.Services.Routing;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// Both calls are best effort: <c>null</c> means not found or provider unavailable, never an exception. Only coarse,
/// city-level places are sent out.
/// </summary>
internal interface IRouteProvider
{
    Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default);

    /// <summary>A GeoJSON LineString through <paramref name="points"/> in order.</summary>
    Task<string?> GetRouteGeoJsonAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default);
}
