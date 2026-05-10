using Humans.Domain.Entities;

namespace Humans.Application.Helpers;

public static class GoogleGroupKeyHelper
{
    public static string? TryGetGroupKey(
        GoogleResource resource,
        string? teamGoogleGroupEmail = null,
        string? configuredDomain = null)
    {
        if (!string.IsNullOrWhiteSpace(teamGoogleGroupEmail))
            return teamGoogleGroupEmail.Trim();

        var urlEmail = TryDeriveGroupEmail(resource, configuredDomain);
        if (!string.IsNullOrWhiteSpace(urlEmail))
            return urlEmail;

        return resource.GoogleId.Contains('@', StringComparison.Ordinal)
            ? resource.GoogleId.Trim()
            : null;
    }

    public static string? TryDeriveGroupEmail(GoogleResource resource, string? configuredDomain = null)
    {
        if (string.IsNullOrWhiteSpace(resource.Url))
            return null;

        const string marker = "/g/";
        var idx = resource.Url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var prefix = resource.Url[(idx + marker.Length)..].TrimEnd('/');
        var beforeGroup = resource.Url[..idx];
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
