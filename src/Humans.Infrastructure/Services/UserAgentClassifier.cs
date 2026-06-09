using MyCSharp.HttpUserAgentParser;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Maps a raw User-Agent string to coarse, bounded family-level buckets
/// (OS, browser, device class) for the <c>/Admin/ClientStats</c> debug screen.
/// Parsing is delegated to MyCSharp.HttpUserAgentParser; this type only
/// translates its output into the small display vocabulary the screen tallies.
/// </summary>
/// <remarks>
/// Deliberately family-level, not version-level: it keeps cardinality bounded and
/// the parser is code-based (no regex-database freshness dependency). Static
/// parsing is fine at this project's scale — no per-parse caching is wired in.
/// </remarks>
public static class UserAgentClassifier
{
    public static ClientClassification Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new ClientClassification("Unknown", "Unknown", "Unknown");

        var info = HttpUserAgentInformation.Parse(userAgent);

        // Bots collapse to a single bucket across all three dimensions so crawler
        // traffic is visible without inflating cardinality. The specific bot name
        // (from the parser's bounded known-bot list) is carried separately so the
        // ClientStats screen can break the "Bot" bucket down by crawler.
        if (info.IsRobot())
        {
            var botName = string.IsNullOrEmpty(info.Name) ? "Other bot" : info.Name!;
            return new ClientClassification("Bot", "Bot", "Bot", botName);
        }

        var os = MapOs(info.Platform?.PlatformType);
        var browser = string.IsNullOrEmpty(info.Name) ? "Unknown" : info.Name!;
        var device = info.IsMobile() ? "Mobile" : "Desktop";
        return new ClientClassification(os, browser, device);
    }

    private static string MapOs(HttpUserAgentPlatformType? platform) => platform switch
    {
        HttpUserAgentPlatformType.Windows => "Windows",
        HttpUserAgentPlatformType.MacOS => "macOS",
        HttpUserAgentPlatformType.IOS => "iOS",
        HttpUserAgentPlatformType.Android => "Android",
        HttpUserAgentPlatformType.Linux => "Linux",
        HttpUserAgentPlatformType.Unix => "Unix",
        HttpUserAgentPlatformType.ChromeOS => "ChromeOS",
        HttpUserAgentPlatformType.BlackBerry => "BlackBerry",
        HttpUserAgentPlatformType.Symbian => "Symbian",
        HttpUserAgentPlatformType.Generic => "Other",
        _ => "Unknown",
    };
}

/// <summary>
/// Coarse family-level classification of one client. <paramref name="BotName"/> is
/// non-null only when the client is a recognised crawler (<see cref="UserAgentClassifier"/>
/// still collapses <paramref name="Os"/>/<paramref name="Browser"/>/<paramref name="Device"/>
/// to <c>"Bot"</c>); it names the specific crawler for the ClientStats breakdown.
/// </summary>
public sealed record ClientClassification(string Os, string Browser, string Device, string? BotName = null);
