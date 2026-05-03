using Humans.Domain.Constants;

namespace Humans.Web.Helpers;

/// <summary>
/// Maps a request path (e.g. captured by the floating-widget submission as
/// <c>PageUrl</c>) to the technical Section name used by
/// <see cref="IssueSectionRouting"/>. Used by the <c>IssuesController</c> to
/// auto-route an issue when the submitter doesn't pick a section themselves.
/// </summary>
public static class IssueSectionInference
{
    /// <summary>
    /// Returns the technical Section name (matching <see cref="IssueSectionRouting"/>)
    /// inferred from a path's first segment, or null if no match.
    /// Examples: "/Camps/123" returns "Camps"; "/Tickets" returns "Tickets"; "/" returns null.
    /// </summary>
    public static string? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var uri))
            return null;

        var p = uri.IsAbsoluteUri ? uri.AbsolutePath : StripQueryAndFragment(path);
        var trimmed = p.Trim('/');
        if (trimmed.Length == 0) return null;

        var first = trimmed.Split('/', 2)[0];
        return Map(first);
    }

    private static string StripQueryAndFragment(string path)
    {
        var qIdx = path.IndexOfAny(['?', '#']);
        return qIdx < 0 ? path : path[..qIdx];
    }

    private static string? Map(string segment) => segment.ToLowerInvariant() switch
    {
        "camps" or "barrios" => IssueSectionRouting.Camps,
        "tickets" => IssueSectionRouting.Tickets,
        "teams" => IssueSectionRouting.Teams,
        "shifts" or "vol" => IssueSectionRouting.Shifts,
        "onboardingreview" => IssueSectionRouting.Onboarding,
        "profile" or "humans" => IssueSectionRouting.Profiles,
        "finance" or "budget" => IssueSectionRouting.Budget,
        "board" or "voting" => IssueSectionRouting.Governance,
        "legal" or "consent" => IssueSectionRouting.Legal,
        "city" => IssueSectionRouting.CityPlanning,
        _ => null
    };
}
