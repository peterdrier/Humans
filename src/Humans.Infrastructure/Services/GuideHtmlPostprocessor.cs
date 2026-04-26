using System.Text.RegularExpressions;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Rewrites &lt;a href&gt; and &lt;img src&gt; attributes in rendered guide HTML so sibling
/// .md links become in-app routes, external/parent-relative links open in a new tab,
/// and image references resolve to raw.githubusercontent.com.
/// </summary>
public sealed class GuideHtmlPostprocessor
{
    private static readonly Regex HrefPattern = new(
        """<a\s+(?<before>[^>]*?)href="(?<url>[^"]+)"(?<rest>[^>]*)>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(500));

    private static readonly Regex ImgPattern = new(
        """<img\s+(?<before>[^>]*?)src="(?<url>[^"]+)"(?<rest>[^>]*)>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(500));

    // Matches <code>/route/path</code> spans where the content is a concrete app path:
    // starts with "/", contains no "{" (so route templates like /Profile/{id} are left
    // alone), no whitespace, no "#" or "?". These spans get wrapped in an <a href>.
    private static readonly Regex AppPathCodePattern = new(
        """<code>(?<path>/[^\s<>{}#?]+)</code>""",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(500));

    public string Rewrite(string html, GuideSettings settings, IReadOnlySet<string> knownFileStems)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(knownFileStems);

        var githubRepoBlobBase =
            $"https://github.com/{settings.Owner}/{settings.Repository}/blob/{settings.Branch}/";
        var rawBase = settings.RawContentBaseUrl;
        var guideFolder = settings.FolderPath.TrimEnd('/') + "/";

        html = HrefPattern.Replace(html, match =>
        {
            var before = match.Groups["before"].Value;
            var url = match.Groups["url"].Value;
            var after = match.Groups["rest"].Value;

            var rewritten = RewriteHref(url, knownFileStems, githubRepoBlobBase, guideFolder);
            if (rewritten.IsExternal && !after.Contains("target=", StringComparison.OrdinalIgnoreCase))
            {
                after = $" target=\"_blank\" rel=\"noopener\"{after}";
            }

            return $"<a {before}href=\"{rewritten.Href}\"{after}>";
        });

        html = ImgPattern.Replace(html, match =>
        {
            var before = match.Groups["before"].Value;
            var url = match.Groups["url"].Value;
            var after = match.Groups["rest"].Value;

            var rewritten = RewriteImgSrc(url, rawBase, guideFolder);
            return $"<img {before}src=\"{rewritten}\"{after}>";
        });

        html = AppPathCodePattern.Replace(html, match =>
        {
            var path = match.Groups["path"].Value;
            return $"""<a href="{path}" class="guide-app-path"><code>{path}</code></a>""";
        });

        return html;
    }

    private static (string Href, bool IsExternal) RewriteHref(
        string url,
        IReadOnlySet<string> knownStems,
        string githubRepoBlobBase,
        string guideFolder)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (url, true);
        }

        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return (url, false);
        }

        if (url.StartsWith('/'))
        {
            return (url, false);
        }

        if (url.StartsWith("../", StringComparison.Ordinal))
        {
            var resolved = ResolveParentRelative(url, guideFolder);
            return ($"{githubRepoBlobBase}{resolved}", true);
        }

        var (path, fragment) = SplitFragment(url);

        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var stem = path[..^3];
            var known = knownStems.FirstOrDefault(s => s.Equals(stem, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                var href = fragment is null ? $"/Guide/{known}" : $"/Guide/{known}#{fragment}";
                return (href, false);
            }

            var blobUrl = $"{githubRepoBlobBase}{guideFolder}{path}";
            if (fragment is not null)
            {
                blobUrl = $"{blobUrl}#{fragment}";
            }
            return (blobUrl, true);
        }

        return (url, false);
    }

    private static string RewriteImgSrc(string url, string rawBase, string guideFolder)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var trimmed = url.TrimStart('/');
        if (trimmed.StartsWith(guideFolder, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[guideFolder.Length..];
        }

        return rawBase + trimmed;
    }

    private static string ResolveParentRelative(string url, string guideFolder)
    {
        var segments = guideFolder.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        var remainder = url;
        while (remainder.StartsWith("../", StringComparison.Ordinal))
        {
            if (segments.Count > 0)
            {
                segments.RemoveAt(segments.Count - 1);
            }
            remainder = remainder[3..];
        }

        segments.Add(remainder);
        return string.Join('/', segments);
    }

    private static (string Path, string? Fragment) SplitFragment(string url)
    {
        var hashIndex = url.IndexOf('#');
        if (hashIndex < 0)
        {
            return (url, null);
        }
        return (url[..hashIndex], url[(hashIndex + 1)..]);
    }
}
