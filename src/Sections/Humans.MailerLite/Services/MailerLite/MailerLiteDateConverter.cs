using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Base.Extensions;
using NodaTime;

namespace Humans.MailerLite.Services.MailerLite;

/// <summary>
/// JSON converter for MailerLite timestamp fields. Format is
/// <c>"YYYY-MM-DD HH:MM:SS"</c> (space separator, no offset). Treated as UTC
/// per ML docs.
/// </summary>
internal sealed class MailerLiteDateConverter : JsonConverter<Instant?>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss";

    public override Instant? Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions __)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var raw = reader.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        var dt = DateTime.ParseExact(raw, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
    }

    public override void Write(Utf8JsonWriter writer, Instant? value, JsonSerializerOptions _)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToDateTimeUtc().ToInvariantTimestamp());
    }
}
