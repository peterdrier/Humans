using Serilog.Core;
using Serilog.Events;

namespace Humans.Infrastructure.Logging;

/// <summary>
/// Serilog enricher that redacts PII values from log event properties.
/// Property names are matched case-insensitively. IDs/GUIDs are left intact for debugging.
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

    private static object RedactValue(string propertyName, LogEventPropertyValue original)
    {
        if (original is not ScalarValue scalar || scalar.Value is not string stringValue)
            return "***";

        if (string.IsNullOrEmpty(stringValue))
            return stringValue;

        // Email-like properties: show first 2 chars + ***@domain
        if (propertyName.Contains("email", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("To", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("UserEmail", StringComparison.OrdinalIgnoreCase))
        {
            var atIndex = stringValue.IndexOf("@", StringComparison.Ordinal);
            if (atIndex > 0)
            {
                var prefix = stringValue[..Math.Min(2, atIndex)];
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

        // Other PII (names, phones, IPs): show first 2 chars + ***
        if (stringValue.Length <= 2)
            return stringValue;

        return $"{stringValue[..2]}***";
    }
}
