using System.Text.Json;
using Humans.Base.Extensions;
using System.Text.Json.Serialization;
using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.MailerLite.Services.MailerLite;

/// <summary>
/// v2 nests the names under <c>fields.name</c> and <c>fields.last_name</c>, so positional binding alone leaves
/// them null.
/// </summary>
internal sealed class MailerLiteSubscriberConverter : JsonConverter<MailerLiteSubscriber>
{
    public override MailerLiteSubscriber Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var id = ReadString(root, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new JsonException("subscriber.id missing");
        var email = ReadString(root, "email");
        if (string.IsNullOrWhiteSpace(email)) throw new JsonException("subscriber.email missing");
        var status = ReadString(root, "status");
        if (string.IsNullOrWhiteSpace(status))
            throw new JsonException("subscriber.status missing");
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

        var groupIds = new List<string>();
        if (root.TryGetProperty("groups", out var groupsEl))
        {
            if (groupsEl.ValueKind != JsonValueKind.Array)
                throw new JsonException("subscriber.groups must be an array");
            foreach (var group in groupsEl.EnumerateArray())
            {
                // ML returns either an array of objects with id+name or an array of id strings.
                var groupId = group.ValueKind switch
                {
                    JsonValueKind.String => group.GetString(),
                    JsonValueKind.Object => ReadString(group, "id"),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(groupId))
                    throw new JsonException("subscriber.groups contains an invalid id");
                groupIds.Add(groupId);
            }
        }

        return new MailerLiteSubscriber(
            id, email, status, source,
            subscribedAt, unsubscribedAt, optedInAt,
            firstName, lastName, groupIds);
    }

    public override void Write(Utf8JsonWriter writer, MailerLiteSubscriber value, JsonSerializerOptions options) =>
        throw new NotSupportedException("MailerLiteSubscriber is read-only");

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static Instant? ParseDate(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
            throw new JsonException($"subscriber.{name} must be a timestamp string or null");
        var raw = el.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        var dt = DateTime.ParseExact(raw, DateFormattingExtensions.MailerLiteTimestampPattern,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
    }
}
