using System.Text.Json;

namespace Humans.Rideshare.Services.Routing;

internal static class RoutingGeometry
{
    internal static bool IsPoint(JsonElement geometry) =>
        HasType(geometry, "Point") && geometry.TryGetProperty("coordinates", out var position) && IsPosition(position) &&
        position[0].GetDouble() is >= -180 and <= 180;

    internal static bool IsLineString(JsonElement geometry) =>
        HasType(geometry, "LineString") && geometry.TryGetProperty("coordinates", out var positions) &&
        positions.ValueKind == JsonValueKind.Array && positions.GetArrayLength() >= 2 &&
        positions.EnumerateArray().All(IsPosition);

    private static bool HasType(JsonElement geometry, string type) =>
        geometry.ValueKind == JsonValueKind.Object && geometry.TryGetProperty("type", out var value) &&
        value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), type, StringComparison.Ordinal);

    private static bool IsPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2) return false;
        foreach (var coordinate in position.EnumerateArray())
            if (coordinate.ValueKind != JsonValueKind.Number || !coordinate.TryGetDouble(out var number) ||
                !double.IsFinite(number)) return false;
        return position[1].GetDouble() is >= -90 and <= 90;
    }
}
