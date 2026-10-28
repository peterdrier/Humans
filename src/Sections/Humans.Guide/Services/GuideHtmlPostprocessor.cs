using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Humans.Base.Configuration;

namespace Humans.Guide.Services;

/// <summary>
/// Sibling .md links become in-app routes, external and parent-relative links open in a new tab, and images resolve
/// to raw.githubusercontent.com.
/// </summary>
internal sealed class GuideHtmlPostprocessor
{
    // Matches <code>/route/path</code> spans where the content is a concrete app path:
    // starts with "/", contains no "{" and no "*" (so route templates like /Profile/{id}
    // and wildcards like /api/backdoor/* are left alone — both would 404 if linked), no
    // whitespace, no "#" or "?". These spans get wrapped in an <a href>.
    private static readonly Regex AppPathCodePattern = new(
        """<code>(?<path>/[^\s<>{}#?*]+)</code>""",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(500));

    public string Rewrite(string html, GuideSettings settings)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(settings);

        var githubRepoBlobBase =
            $"https://github.com/{settings.Owner}/{settings.Repository}/blob/{settings.Branch}/";
        var rawBase = settings.RawContentBaseUrl;
        var guideFolder = settings.FolderPath.TrimEnd('/') + "/";

        using var document = new HtmlParser().ParseDocument(string.Empty);
        if (document.Body is not { } body)
            return html;
        // Parse as a fragment: document parsing moves leading styles out of the body we return.
        body.InnerHtml = html;

        var modified = false;
        foreach (var link in body.QuerySelectorAll("a[href]"))
        {
            var url = link.GetAttribute("href")!;
            if (url.Length == 0) continue;
            var rewritten = RewriteHref(url, githubRepoBlobBase, guideFolder);
            if (!string.Equals(rewritten.Href, url, StringComparison.Ordinal))
            {
                link.SetAttribute("href", rewritten.Href);
                modified = true;
            }
            if (!rewritten.IsExternal) continue;

            if (!link.HasAttribute("target"))
            {
                link.SetAttribute("target", "_blank");
                modified = true;
            }
            if (string.Equals(link.GetAttribute("target"), "_blank", StringComparison.OrdinalIgnoreCase))
            {
                var rel = link.GetAttribute("rel")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (!rel.Contains("noopener", StringComparer.OrdinalIgnoreCase))
                {
                    link.SetAttribute("rel", string.Join(' ', rel.Append("noopener")));
                    modified = true;
                }
            }
        }

        foreach (var image in body.QuerySelectorAll("img[src]"))
        {
            var url = image.GetAttribute("src")!;
            if (url.Length == 0) continue;
            var rewritten = RewriteImgSrc(url, rawBase, guideFolder);
            if (string.Equals(rewritten, url, StringComparison.Ordinal)) continue;
            image.SetAttribute("src", rewritten);
            modified = true;
        }

        foreach (var code in body.QuerySelectorAll("code"))
        {
            // Existing links own their destination; block examples are not navigation.
            if (code.Closest("a, pre") is not null || !AppPathCodePattern.IsMatch(code.OuterHtml))
                continue;

            var anchor = document.CreateElement("a");
            anchor.SetAttribute("href", code.TextContent);
            anchor.ClassName = "guide-app-path";
            code.Parent?.ReplaceChild(anchor, code);
            anchor.AppendChild(code);
            modified = true;
        }

        return modified ? body.InnerHtml : html;
    }

    private static (string Href, bool IsExternal) RewriteHref(
        string url,
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
            if (GuideFiles.TryCanonical(stem, out var known))
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
