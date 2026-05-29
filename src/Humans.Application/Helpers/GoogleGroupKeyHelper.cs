using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Entities;

namespace Humans.Application.Helpers;

public static class GoogleGroupKeyHelper
{
    public static string? TryGetGroupKey(
        GoogleResource resource,
        string? teamGoogleGroupEmail = null,
        string? configuredDomain = null)
        => TryGetGroupKey(resource.GoogleId, resource.Url, teamGoogleGroupEmail, configuredDomain);

    public static string? TryGetGroupKey(
        GoogleResourceSnapshot resource,
        string? teamGoogleGroupEmail = null,
        string? configuredDomain = null)
        => TryGetGroupKey(resource.GoogleId, resource.Url, teamGoogleGroupEmail, configuredDomain);

    private static string? TryGetGroupKey(
        string googleId,
        string? url,
        string? teamGoogleGroupEmail,
        string? configuredDomain)
    {
        if (!string.IsNullOrWhiteSpace(teamGoogleGroupEmail))
            return teamGoogleGroupEmail.Trim();

        var urlEmail = TryDeriveGroupEmail(url, configuredDomain);
        if (!string.IsNullOrWhiteSpace(urlEmail))
            return urlEmail;

        return googleId.Contains('@', StringComparison.Ordinal)
            ? googleId.Trim()
            : null;
    }

    private static string? TryDeriveGroupEmail(string? url, string? configuredDomain = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        const string marker = "/g/";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var prefix = url[(idx + marker.Length)..].TrimEnd('/');
        var beforeGroup = url[..idx];
        const string domainMarker = "/a/";
        var domainIdx = beforeGroup.LastIndexOf(domainMarker, StringComparison.Ordinal);
        var domain = domainIdx < 0
            ? configuredDomain?.Trim()
            : beforeGroup[(domainIdx + domainMarker.Length)..];
        return string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(domain)
            ? null
            : $"{prefix}@{domain}";
    }
}
