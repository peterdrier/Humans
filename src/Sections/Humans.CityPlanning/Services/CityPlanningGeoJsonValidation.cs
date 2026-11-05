using System.Text.Json;

namespace Humans.CityPlanning.Services;

internal static class CityPlanningGeoJsonValidation
{
    internal static bool IsValidCampGeometry(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var geometry = document.RootElement;
            if (geometry.ValueKind != JsonValueKind.Object) return false;
            if (HasType(geometry, "Feature"))
            {
                if (!geometry.TryGetProperty("geometry", out geometry) ||
                    geometry.ValueKind != JsonValueKind.Object) return false;
            }
            if (!geometry.TryGetProperty("coordinates", out var coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0) return false;
            if (HasType(geometry, "Polygon")) return IsPolygon(coordinates);
            if (!HasType(geometry, "MultiPolygon")) return false;
            return coordinates.EnumerateArray().All(IsPolygon);
        }
        catch (JsonException)
        {
            return false;
        }

    }

    private static bool HasType(JsonElement element, string type) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("type", out var value) &&
        value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), type, StringComparison.Ordinal);

    private static bool IsPolygon(JsonElement rings)
    {
        if (rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() == 0) return false;
        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4 ||
                !ring.EnumerateArray().All(IsPosition)) return false;
            var first = ring[0];
            var last = ring[ring.GetArrayLength() - 1];
            if (first.GetArrayLength() != last.GetArrayLength()) return false;
            for (var i = 0; i < first.GetArrayLength(); i++)
                if (first[i].GetDouble() != last[i].GetDouble()) return false;
        }
        return true;
    }

    private static bool IsPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2) return false;
        foreach (var coordinate in position.EnumerateArray())
            if (coordinate.ValueKind != JsonValueKind.Number ||
                !coordinate.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
        return position[0].GetDouble() is >= -180 and <= 180 && position[1].GetDouble() is >= -90 and <= 90;
    }

    internal static bool IsValidOverlayGeoJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (HasType(root, "FeatureCollection"))
                return root.TryGetProperty("features", out var features) && IsArrayOf(features, IsFeature);
            return HasType(root, "Feature") ? IsFeature(root) : IsGeometry(root);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsFeature(JsonElement feature) =>
        HasType(feature, "Feature") && feature.TryGetProperty("geometry", out var geometry) &&
        (geometry.ValueKind == JsonValueKind.Null || IsGeometry(geometry)) &&
        (!feature.TryGetProperty("properties", out var properties) ||
            properties.ValueKind is JsonValueKind.Object or JsonValueKind.Null);

    private static bool IsGeometry(JsonElement geometry)
    {
        if (HasType(geometry, "GeometryCollection"))
            return geometry.TryGetProperty("geometries", out var parts) && IsArrayOf(parts, IsGeometry);
        if (geometry.ValueKind != JsonValueKind.Object || !geometry.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || !geometry.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array) return false;

        // Empty coordinates are permitted for overlay geometries; camp placement still requires a nonempty area.
        return type.GetString() switch
        {
            "Point" => coordinates.GetArrayLength() == 0 || IsPosition(coordinates),
            "MultiPoint" => IsArrayOf(coordinates, IsPosition),
            "LineString" => IsLineString(coordinates),
            "MultiLineString" => IsArrayOf(coordinates, IsLineString),
            "Polygon" => coordinates.GetArrayLength() == 0 || IsPolygon(coordinates),
            "MultiPolygon" => IsArrayOf(coordinates, rings =>
                rings.ValueKind == JsonValueKind.Array && (rings.GetArrayLength() == 0 || IsPolygon(rings))),
            _ => false
        };
    }

    private static bool IsLineString(JsonElement positions) =>
        positions.ValueKind == JsonValueKind.Array &&
        (positions.GetArrayLength() == 0 || positions.GetArrayLength() >= 2 && positions.EnumerateArray().All(IsPosition));

    private static bool IsArrayOf(JsonElement value, Func<JsonElement, bool> accepts) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(accepts);
}
