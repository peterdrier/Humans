using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NodaTime;

namespace Humans.Backdoor.OAuth;

/// <summary>Converts between OpenIddict's value types and the columns the Backdoor stores keep.</summary>
internal static class OAuthStoreValues
{
    public static ImmutableDictionary<string, JsonElement> ReadProperties(string? json) =>
        string.IsNullOrEmpty(json)
            ? ImmutableDictionary<string, JsonElement>.Empty
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToImmutableDictionary(StringComparer.Ordinal);

    public static string? WriteProperties(ImmutableDictionary<string, JsonElement> properties) =>
        properties.IsEmpty ? null : JsonSerializer.Serialize(properties);

    public static DateTimeOffset? ToDateTimeOffset(Instant? instant) => instant?.ToDateTimeOffset();

    public static Instant? ToInstant(DateTimeOffset? value) => value is { } v ? Instant.FromDateTimeOffset(v) : null;

    public static Guid? ParseId(string? identifier) =>
        Guid.TryParse(identifier, CultureInfo.InvariantCulture, out var id) ? id : null;

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    public static string? FormatId(Guid? id) => id?.ToString("D", CultureInfo.InvariantCulture);
}
