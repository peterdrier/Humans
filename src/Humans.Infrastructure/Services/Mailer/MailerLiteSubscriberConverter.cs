using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Infrastructure.Services.Mailer;

/// <summary>
/// Maps a v2 MailerLite subscriber JSON object onto our positional record.
/// FirstName and LastName are nested under <c>fields.name</c> and
/// <c>fields.last_name</c> in v2 — not top-level — so positional record
/// auto-binding alone leaves them null.
/// </summary>
public sealed class MailerLiteSubscriberConverter : JsonConverter<MailerLiteSubscriber>
{
    private static readonly MailerLiteDateConverter DateConverter = new();

    public override MailerLiteSubscriber Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string id = root.GetProperty("id").GetString() ?? throw new JsonException("subscriber.id missing");
        string email = root.GetProperty("email").GetString() ?? throw new JsonException("subscriber.email missing");
        string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        string source = root.TryGetProperty("source", out var sr) ? sr.GetString() ?? "" : "";

        Instant? subscribedAt = ParseDate(root, "subscribed_at");
        Instant? unsubscribedAt = ParseDate(root, "unsubscribed_at");
        Instant? optedInAt = ParseDate(root, "opted_in_at");

        string? firstName = null;
        string? lastName = null;
        if (root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            firstName = ReadString(fields, "name");
            lastName = ReadString(fields, "last_name");
        }

        return new MailerLiteSubscriber(
            id, email, status, source,
            subscribedAt, unsubscribedAt, optedInAt,
            firstName, lastName);
    }

    public override void Write(Utf8JsonWriter writer, MailerLiteSubscriber value, JsonSerializerOptions options) =>
        throw new NotSupportedException("MailerLiteSubscriber is read-only");

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static Instant? ParseDate(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var raw = el.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        var dt = System.DateTime.ParseExact(raw, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        return Instant.FromDateTimeUtc(System.DateTime.SpecifyKind(dt, System.DateTimeKind.Utc));
    }
}
