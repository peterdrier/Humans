using Humans.Base.Interfaces;

namespace Humans.Web.Services;

/// <summary>
/// Names the section a login-redirect target belongs to, so the login page's link preview
/// (Open Graph tags) can say "Shifts" instead of "Sign In" when a deep link is shared into a
/// chat app. Chat crawlers fetch anonymously, land on <c>/Account/Login?ReturnUrl=…</c>, and
/// read whatever that page's head says — this is the only place they ever reach.
/// </summary>
/// <remarks>
/// Matches the first path segment of <c>ReturnUrl</c> against the first segment of each
/// member-nav link's URL (<see cref="ISectionNav"/> contributions, children included), so the
/// mapping tracks the nav instead of a hand-kept list. Only local URLs match; nothing about
/// the target beyond its section ever reaches the preview.
/// </remarks>
public static class LoginLinkPreview
{
    /// <summary>
    /// The SharedResource label key of the nav item whose section owns <paramref name="returnUrl"/>,
    /// or <c>null</c> when the URL is not local or no nav link shares its first segment.
    /// </summary>
    /// <param name="returnUrl">The login redirect target, as received.</param>
    /// <param name="items">The member-nav contributions to match against.</param>
    /// <param name="urlFor">Resolves a nav item's (controller, action) to its path.</param>
    public static string? SectionLabelFor(
        string? returnUrl,
        IEnumerable<MemberNavItem> items,
        Func<string, string, string?> urlFor)
    {
        var target = FirstSegment(returnUrl);
        if (target is null)
            return null;

        foreach (var item in Flatten(items))
        {
            if (item.Divider)
                continue;

            var href = item.RawHref
                ?? (item.Controller is not null && item.Action is not null
                    ? urlFor(item.Controller, item.Action)
                    : null);

            if (string.Equals(FirstSegment(href), target, StringComparison.OrdinalIgnoreCase))
                return item.Label;
        }

        return null;
    }

    private static IEnumerable<MemberNavItem> Flatten(IEnumerable<MemberNavItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.Children is { Count: > 0 })
            {
                foreach (var child in item.Children)
                    yield return child;
            }
        }
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
