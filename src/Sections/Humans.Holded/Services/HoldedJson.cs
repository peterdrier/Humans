using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Holded.Services;

internal static class HoldedJson
{
    internal const string UnconfirmedPaymentRefPrefix = "unconfirmed:";

    /// <summary>Holded applies every key a POST/PUT carries, so <c>"code":null</c> would blank a stored tax id.
    /// Omitting nulls means "leave it alone"; nothing here clears a Holded field on purpose.</summary>
    internal static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // v2 ledger-entries dates arrive as DD/MM/YYYY and filter on the accounting date; parsed to Madrid midnight.
    internal static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>Null when <paramref name="node"/> is not an object. The raw indexer throws there, which on a list
    /// read costs the whole page rather than one field.</summary>
    internal static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject obj ? obj[name] : null;

    /// <summary>Empty when absent or not an array: <see cref="JsonNode.AsArray"/> throws on a non-array, and Holded
    /// will send an absent collection as a scalar.</summary>
    internal static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];

    internal static IReadOnlyList<string> ReadTags(JsonNode? node) =>
        Arr(node).Where(t => t is not null).Select(t => t!.GetValue<string>()).ToList();

    internal static string ReadEuroCurrency(JsonNode? node)
    {
        var currency = node?.GetValue<string>() ?? "eur";
        if (!string.Equals(currency, "eur", StringComparison.OrdinalIgnoreCase))
            throw new HoldedPermanentException("Holded document currency must be EUR.");
        return currency;
    }

    // v2 sends decimals as strings (e.g. "121.00").
    internal static decimal ReadDecimalV2(JsonNode? node) =>
        decimal.Parse(node?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);

    // Holded may encode an integer as a JSON float (40000001.0). Accept that shape,
    // but never truncate a fractional identifier onto a different account or ledger row.
    internal static int? ReadInt(JsonNode? node)
    {
        if (node is null) return null;
        var value = node.GetValue<decimal>();
        if (value != decimal.Truncate(value))
            throw new FormatException("Holded integer field contains a fractional value.");
        return (int)value;
    }

    internal static int ReadRequiredInt(JsonNode? node, string field) =>
        ReadInt(node) ?? throw new HoldedPermanentException(
            $"Holded item is missing required field '{field}' — refusing the page.");

    /// <summary>Null, absent and blank all fail the page: Holded has been seen sending <c>""</c> for a field it
    /// usually fills.</summary>
    internal static string ReadRequiredString(JsonNode? node, string field) =>
        node?.GetValue<string>() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new HoldedPermanentException(
                $"Holded item is missing required field '{field}' — refusing the page.");

    /// <summary>An absent amount must fail the page, not read as 0.00: replace semantics would overwrite the real
    /// amount and still report success.</summary>
    internal static decimal ReadRequiredDecimalV2(JsonNode? node, string field) =>
        node is null
            ? throw new HoldedPermanentException(
                $"Holded item is missing required field '{field}' — refusing the page.")
            : ReadDecimalV2(node);
}
