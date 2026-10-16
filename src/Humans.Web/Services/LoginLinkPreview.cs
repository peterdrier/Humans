namespace Humans.Web.Services;

/// <summary>
/// Names the area a login-redirect target belongs to, so the login page's link preview
/// (Open Graph tags) can describe the shared deep link instead of saying "Sign In". Chat
/// crawlers fetch anonymously, land on <c>/Account/Login?ReturnUrl=…</c>, and read whatever
/// that page's head says — this is the only page they ever reach.
/// </summary>
/// <remarks>
/// The first path segment of <c>ReturnUrl</c> is matched (case-insensitively) against
/// <see cref="Segments"/>; each entry has a <c>LinkPreview_{Segment}_Title</c> and
/// <c>LinkPreview_{Segment}_Description</c> key in SharedResource. Adding a top-level route
/// means adding it here plus the two keys in all six cultures. Only local URLs match; nothing
/// about the target beyond its area ever reaches the preview.
/// </remarks>
public static class LoginLinkPreview
{
    /// <summary>Top-level route segments with preview text, in canonical casing.</summary>
    public static readonly IReadOnlyList<string> Segments =
    [
        "About", "Agent", "Barrios", "Budget", "Calendar", "Camp", "Camps", "Cantina",
        "CityPlanning", "Consent", "Events", "Expenses", "Feedback", "Finance", "Gate",
        "Governance", "Guide", "Issues", "Legal", "Notifications", "Profile", "Rideshare",
        "Search", "Settings", "Shifts", "Store", "Survey", "Teams", "Tickets", "Tour",
        "Welcome", "Workgroups"
    ];

    /// <summary>
    /// The canonical segment that owns <paramref name="returnUrl"/>, or <c>null</c> when the
    /// URL is not local or its first segment has no preview text.
    /// </summary>
    public static string? SegmentFor(string? returnUrl)
    {
        var target = FirstSegment(returnUrl);
        if (target is null)
            return null;

        return Segments.FirstOrDefault(s => string.Equals(s, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>First path segment of a local URL (<c>/Shifts/42</c> → <c>Shifts</c>); null otherwise.</summary>
    private static string? FirstSegment(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/' || url.StartsWith("//", StringComparison.Ordinal))
            return null;

        var path = url.AsSpan(1);
        var end = path.IndexOfAny('/', '?', '#');
        var segment = end < 0 ? path : path[..end];
        return segment.IsEmpty ? null : segment.ToString();
    }
}
