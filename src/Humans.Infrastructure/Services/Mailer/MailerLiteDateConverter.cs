using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;

namespace Humans.Infrastructure.Services.Mailer;

/// <summary>
/// JSON converter for MailerLite timestamp fields. Format is
/// <c>"YYYY-MM-DD HH:MM:SS"</c> (space separator, no offset). Treated as UTC
/// per ML docs.
/// </summary>
public sealed class MailerLiteDateConverter : JsonConverter<Instant?>
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
        else writer.WriteStringValue(value.Value.ToDateTimeUtc().ToString(Format, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Non-nullable counterpart to <see cref="MailerLiteDateConverter"/>. STJ
/// converters are matched on the exact CLR type, so DTOs with required
/// <c>Instant</c> fields (e.g. <c>MailerLiteGroup.CreatedAt</c>) need this
/// variant in addition to the nullable one.
/// </summary>
public sealed class MailerLiteRequiredDateConverter : JsonConverter<Instant>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss";

    public override Instant Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions __)
    {
        var raw = reader.GetString()
            ?? throw new JsonException("MailerLite required timestamp was null");
        var dt = DateTime.ParseExact(raw, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
    }

    public override void Write(Utf8JsonWriter writer, Instant value, JsonSerializerOptions _) =>
        writer.WriteStringValue(value.ToDateTimeUtc().ToString(Format, CultureInfo.InvariantCulture));
}
