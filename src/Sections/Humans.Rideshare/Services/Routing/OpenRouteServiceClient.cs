using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Humans.Rideshare.Services.Routing;

/// <summary>
/// <see cref="IRouteProvider"/> over the OpenRouteService REST API (OSM-based: routing and
/// geocoding in one place, geometry freely storable). Read-only outbound calls, so no
/// <c>[ExternalWrite]</c>. Every failure — blank key, non-success HTTP, malformed body,
/// transport error — logs a warning and returns <c>null</c>; routing never blocks a save.
/// </summary>
internal sealed class OpenRouteServiceClient(
    HttpClient httpClient,
    IOptions<RouteProviderOptions> options,
    ILogger<OpenRouteServiceClient> logger) : IRouteProvider
{
    private const int BodyLogLimit = 500;
    private bool _missingKeyWarned;

    public async Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default)
    {
        var key = ApiKeyOrNull();
        if (key is null || string.IsNullOrWhiteSpace(query)) return null;

        var url = $"geocode/search?api_key={Uri.EscapeDataString(key)}&text={Uri.EscapeDataString(query)}&size=1";
        try
        {
            using var response = await httpClient.GetAsync(url, ct);
            if (!await IsSuccessAsync(response, "geocode", ct)) return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var features = doc.RootElement.GetProperty("features");
            if (features.GetArrayLength() == 0) return null;

            // GeoJSON order: [longitude, latitude].
            var coordinates = features[0].GetProperty("geometry").GetProperty("coordinates");
            return new GeoPoint(coordinates[1].GetDouble(), coordinates[0].GetDouble());
        }
        catch (Exception ex) when (IsProviderFailure(ex, ct))
        {
            logger.LogWarning(ex, "OpenRouteService geocode failed for {Query}", query);
            return null;
        }
    }

    public async Task<string?> GetRouteGeoJsonAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default)
    {
        var key = ApiKeyOrNull();
        if (key is null || points.Count < 2) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v2/directions/driving-car/geojson");
            request.Headers.TryAddWithoutValidation("Authorization", key);
            request.Content = JsonContent.Create(new DirectionsRequest(
                points.Select(p => new[] { p.Longitude, p.Latitude }).ToArray()));

            using var response = await httpClient.SendAsync(request, ct);
            if (!await IsSuccessAsync(response, "directions", ct)) return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var features = doc.RootElement.GetProperty("features");
            if (features.GetArrayLength() == 0) return null;

            return features[0].GetProperty("geometry").GetRawText();
        }
        catch (Exception ex) when (IsProviderFailure(ex, ct))
        {
            logger.LogWarning(ex, "OpenRouteService directions failed for {PointCount} points", points.Count);
            return null;
        }
    }

    private string? ApiKeyOrNull()
    {
        var key = options.Value.ApiKey;
        if (!string.IsNullOrWhiteSpace(key)) return key;

        if (!_missingKeyWarned)
        {
            _missingKeyWarned = true;
            logger.LogWarning("ORS_API_KEY is not set; rideshare geocoding and routing are disabled");
        }
        return null;
    }

    private async Task<bool> IsSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return true;

        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogWarning(
            "OpenRouteService {Operation} returned {StatusCode}: {Body}",
            operation, (int)response.StatusCode,
            body.Length > BodyLogLimit ? body[..BodyLogLimit] : body);
        return false;
    }

    // Genuine caller cancellation propagates; everything else (transport, timeout,
    // malformed or unexpected JSON) is the provider being unavailable.
    private static bool IsProviderFailure(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;

    // ORS expects lower-case "coordinates"; JsonContent's default web options camel-case it.
    private sealed record DirectionsRequest(double[][] Coordinates);
}
