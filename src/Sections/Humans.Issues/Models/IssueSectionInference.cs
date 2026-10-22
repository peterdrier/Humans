using Humans.Issues.Domain;

namespace Humans.Issues.Models;

/// <summary>Auto-routes an issue whose submitter didn't pick a section.</summary>
internal static class IssueSectionInference
{
    /// <summary>From the path's first segment: "/Camps/123" gives "Camps"; "/" gives null.</summary>
    public static string? FromPath(string? path, IssueSectionRouting routing)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var uri))
            return null;

        var p = uri.IsAbsoluteUri ? uri.AbsolutePath : StripQueryAndFragment(path);
        var trimmed = p.Trim('/');
        if (trimmed.Length == 0) return null;

        var first = trimmed.Split('/', 2)[0];
        return routing.ResolvePathSegment(first);
    }

    private static string StripQueryAndFragment(string path)
    {
        var qIdx = path.IndexOfAny(['?', '#']);
        return qIdx < 0 ? path : path[..qIdx];
    }
}
