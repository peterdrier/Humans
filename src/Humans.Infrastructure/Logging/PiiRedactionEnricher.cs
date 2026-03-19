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

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var keysToRedact = new List<string>();

        foreach (var property in logEvent.Properties)
        {
            if (ShouldRedact(property.Key))
            {
                keysToRedact.Add(property.Key);
            }
        }

        foreach (var key in keysToRedact)
        {
            var original = logEvent.Properties[key];
            var redacted = RedactValue(key, original);
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, redacted));
        }
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
