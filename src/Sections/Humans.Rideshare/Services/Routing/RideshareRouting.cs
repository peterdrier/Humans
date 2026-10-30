using Humans.Rideshare.Domain;

namespace Humans.Rideshare.Services.Routing;

internal sealed class RideshareRouting(IRouteProvider routeProvider, ILogger logger)
{
    public async Task<(GeoPoint Point, RideshareRefusal? Refusal)> ResolvePointAsync(
        string label, double? latitude, double? longitude, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(label))
            return (default, new("Rideshare_Error_PlaceRequired"));
        if (latitude is { } lat && longitude is { } lng)
            return (new GeoPoint(lat, lng), null);

        var point = await routeProvider.GeocodeAsync(label.Trim(), ct);
        return point is { } resolved
            ? (resolved, null)
            : (default, new("Rideshare_Error_PlaceNotFound", label.Trim()));
    }

    /// <summary>Geocodes each label; a label already on the trip keeps its point (no provider call).</summary>
    public async Task<(IReadOnlyList<Waypoint> Waypoints, RideshareRefusal? Refusal)> GeocodeWaypointsAsync(
        IReadOnlyList<string> labels, IReadOnlyList<Waypoint> existing, CancellationToken ct)
    {
        var result = new List<Waypoint>();
        foreach (var raw in labels)
        {
            var label = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (label is null) continue;

            var known = existing.FirstOrDefault(w => string.Equals(w.Label, label, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                result.Add(known);
                continue;
            }

            var point = await routeProvider.GeocodeAsync(label, ct);
            if (point is null) return ([], new("Rideshare_Error_StopNotFound", label));
            result.Add(new Waypoint(label, point.Value.Latitude, point.Value.Longitude));
        }
        return (result, null);
    }

    /// <summary>
    /// Inbound: member → waypoints → destination. Outbound: destination → waypoints → member.
    /// Waypoints are stored in travel order, so they read the same way in both directions.
    /// A null route is stored as null and never blocks the save.
    /// </summary>
    public async Task<string?> RouteAsync(
        RideshareDirection direction, GeoPoint member, IReadOnlyList<Waypoint> waypoints, GeoPoint destination, CancellationToken ct)
    {
        var points = new List<GeoPoint>(waypoints.Count + 2);
        points.Add(direction == RideshareDirection.Inbound ? member : destination);
        points.AddRange(waypoints.Select(w => new GeoPoint(w.Latitude, w.Longitude)));
        points.Add(direction == RideshareDirection.Inbound ? destination : member);

        var route = await routeProvider.GetRouteGeoJsonAsync(points, ct);
        if (route is null)
        {
            logger.LogWarning(
                "No route for {Direction} trip from {Label}; saving without geometry",
                direction, direction == RideshareDirection.Inbound ? "member point" : "destination");
        }
        return route;
    }
}
