namespace Humans.Domain.Helpers;

public static class PlatformDetector
{
    public record PlatformInfo(string Name, string IconClass);

    private static readonly Dictionary<string, PlatformInfo> KnownPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["instagram.com"] = new("Instagram", "fa-brands fa-instagram"),
        ["facebook.com"] = new("Facebook", "fa-brands fa-facebook"),
        ["x.com"] = new("X", "fa-brands fa-x-twitter"),
        ["twitter.com"] = new("X", "fa-brands fa-x-twitter"),
        ["tiktok.com"] = new("TikTok", "fa-brands fa-tiktok"),
        ["discord.gg"] = new("Discord", "fa-brands fa-discord"),
        ["discord.com"] = new("Discord", "fa-brands fa-discord"),
        ["youtube.com"] = new("YouTube", "fa-brands fa-youtube"),
        ["linkedin.com"] = new("LinkedIn", "fa-brands fa-linkedin"),
    };

    public static PlatformInfo Detect(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new PlatformInfo(string.Empty, "fa-solid fa-link");

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        if (KnownPlatforms.TryGetValue(host, out var platform))
            return platform;

        return new PlatformInfo(host, "fa-solid fa-link");
    }

    public static bool IsSocialMedia(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        return KnownPlatforms.ContainsKey(host);
    }
}
