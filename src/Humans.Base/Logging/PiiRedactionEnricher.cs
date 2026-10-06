using Serilog.Core;
using Serilog.Events;

namespace Humans.Base.Logging;

/// <summary>
/// Serilog enricher that redacts PII values from log event properties.
/// Property names are matched case-insensitively. IDs/GUIDs are left intact for debugging.
/// Retained prefixes stay within two UTF-16 units without splitting surrogate pairs.
/// </summary>
public sealed class PiiRedactionEnricher : ILogEventEnricher
{
    private static readonly HashSet<string> ExactPiiProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Email",
        "EmailAddress",
        "UserEmail",
        "UserName",
        "Name",
        "Phone",
        "PhoneNumber",
        "IpAddress",
        "RemoteIp",
        "To",
        "Recipient",
    };

    private const string ICalFeedPathPrefix = "/api/ical/";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var keysToRedact = new List<string>();
        var pathsToScrub = new List<(string Key, string Scrubbed)>();

        foreach (var property in logEvent.Properties)
        {
            if (ShouldRedact(property.Key))
            {
                keysToRedact.Add(property.Key);
            }
            else if (IsRequestPathProperty(property.Key) &&
                     property.Value is ScalarValue { Value: string path } &&
                     ScrubICalFeedToken(path) is { } scrubbed)
            {
                pathsToScrub.Add((property.Key, scrubbed));
            }
        }

        foreach (var key in keysToRedact)
        {
            var original = logEvent.Properties[key];
            var redacted = RedactValue(key, original);
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, redacted));
        }

        foreach (var (key, scrubbed) in pathsToScrub)
        {
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, scrubbed));
        }
    }

    private static bool IsRequestPathProperty(string propertyName)
    {
        return propertyName.Equals("RequestPath", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Path", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The iCal feed URL carries the user's feed token as a path segment
    /// (<c>/api/ical/{userId}/{token}.ics</c>). Keep the userId (IDs stay
    /// intact for debugging) but redact the token so request logs aren't a
    /// credential store. Returns null when the path isn't an iCal feed path.
    /// </summary>
    private static string? ScrubICalFeedToken(string path)
    {
        if (!path.StartsWith(ICalFeedPathPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var userIdEnd = path.IndexOf('/', ICalFeedPathPrefix.Length);
        if (userIdEnd < 0)
            return null; // no token segment present — nothing to scrub

        return $"{path[..userIdEnd]}/[redacted].ics";
    }

    private static bool ShouldRedact(string propertyName)
    {
        if (ExactPiiProperties.Contains(propertyName))
            return true;

        return propertyName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase);
    }

    private static string RetainedPrefix(string value, int length)
    {
        if (length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }

    private static object RedactValue(string propertyName, LogEventPropertyValue original)
    {
        if (original is not ScalarValue scalar || scalar.Value is not string stringValue)
            return "***";

        if (string.IsNullOrEmpty(stringValue))
            return stringValue;

        // Email-like properties: retain up to 2 UTF-16 units + ***@domain
        if (propertyName.Contains("email", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("To", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("Recipient", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("UserEmail", StringComparison.OrdinalIgnoreCase))
        {
            var atIndex = stringValue.IndexOf("@", StringComparison.Ordinal);
            if (atIndex > 0)
            {
                var prefix = RetainedPrefix(stringValue, Math.Min(2, atIndex));
                var domain = stringValue[(atIndex + 1)..];
                return $"{prefix}***@{domain}";
            }
        }

        // Password/secret: fully redact
        if (propertyName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return "***";
        }

        // Other PII (names, phones, IPs): retain up to 2 UTF-16 units + ***
        if (stringValue.Length <= 2)
            return stringValue;

        return $"{RetainedPrefix(stringValue, 2)}***";
    }
}
