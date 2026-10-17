using Humans.Issues.Domain;

namespace Humans.Issues.Models;

/// <summary>
/// Maps a request path (e.g. captured by the floating-widget submission as
/// <c>PageUrl</c>) to the technical Section name used by
/// <see cref="IssueSectionRouting"/>. Used by the <c>IssuesController</c> to
/// auto-route an issue when the submitter doesn't pick a section themselves.
/// </summary>
internal static class IssueSectionInference
{
    /// <summary>
    /// Returns the technical Section name (matching <see cref="IssueSectionRouting"/>)
    /// inferred from a path's first segment, or null if no match.
    /// Examples: "/Camps/123" returns "Camps"; "/Tickets" returns "Tickets"; "/" returns null.
    /// </summary>
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
