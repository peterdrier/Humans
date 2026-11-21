using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Humans.HiEvents.Services;

internal static class HiEventsWebhookSignature
{
    public static bool IsValid(string body, string signature, string secret)
    {
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    /// <summary>The event and order an <c>order.*</c> event is about; null for any other event or an unreadable body.</summary>
    public static (int EventId, int OrderId)? ReadOrder(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("event_type", out var type)
                || type.GetString()?.StartsWith("order.", StringComparison.Ordinal) != true
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("id", out var id)
                || !payload.TryGetProperty("event_id", out var eventId))
            {
                return null;
            }

            return (eventId.GetInt32(), id.GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
