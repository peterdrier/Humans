using Humans.Application.DTOs;

namespace Humans.Web.Extensions;

/// <summary>
/// Caller-side helper for the broad-search UI: given a query and a
/// <see cref="HumanSearchResult"/> returned from
/// <c>IProfileService.SearchProfilesAsync</c>, determines which field matched
/// (display name, burner name, city, interests, bio, pronouns) and a short
/// snippet for free-text fields.
/// </summary>
/// <remarks>
/// Match precedence is display-name-wins, mirroring the legacy
/// <c>SearchHumansAsync</c> behaviour. CV-only matches are not detected here
/// because <see cref="HumanSearchResult"/> does not carry CV entries; the
/// broad search predicate still surfaces the row, the badge just won't
/// label it as a CV match.
/// </remarks>
public static class HumanSearchMatcher
{
    public static (string? Field, string? Snippet) DetermineMatch(HumanSearchResult r, string query)
    {
        if (r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return ("Name", null);
        if (r.BurnerName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Burner Name", null);
        if (r.City?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("City", r.City);
        if (r.ContributionInterests?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Interests", GetSnippet(r.ContributionInterests, query));
        if (r.Bio?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Bio", GetSnippet(r.Bio, query));
        if (r.Pronouns?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Pronouns", r.Pronouns);

        return (null, null);
    }

    private static string GetSnippet(string text, string query, int contextChars = 60)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return text.Length <= contextChars * 2 ? text : text[..(contextChars * 2)] + "...";

        var start = Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index + query.Length + contextChars);
        var snippet = text[start..end];
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }
}
