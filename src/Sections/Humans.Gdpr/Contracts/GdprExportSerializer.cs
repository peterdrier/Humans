using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Humans.Gdpr.Contracts;

/// <summary>Serializes the shared Article 15 download document.</summary>
public static class GdprExportSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Returns indented UTF-8 JSON with envelope fields followed by section slices.</summary>
    public static byte[] Serialize(GdprExport export)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExportedAt"] = export.ExportedAt,
            ["UserId"] = export.UserId,
            ["MergedFromUserIds"] = export.MergedFromUserIds
        };
        foreach (var (section, data) in export.Sections)
        {
            payload[section] = data;
        }
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Options));
    }
}
