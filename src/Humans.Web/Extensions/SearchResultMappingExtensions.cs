using Humans.Application.DTOs;
using Humans.Web.Models;

namespace Humans.Web.Extensions;

public static class SearchResultMappingExtensions
{
    /// <summary>
    /// Canonical display order for person-search results: relevance score descending
    /// (exact-name &gt; prefix &gt; token-prefix &gt; contains &gt; non-name field), then
    /// BurnerName as a stable alphabetical tiebreak. The service returns matches unordered
    /// (memory/architecture/person-search.md); every people-search surface orders through here
    /// so the literal "Ian" lands above "Adrian"/"Brian" instead of one alphabetical list.
    /// </summary>
    public static IOrderedEnumerable<HumanSearchResult> OrderByRelevance(
        this IEnumerable<HumanSearchResult> results) =>
        results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase);

    public static HumanSearchResultViewModel ToHumanSearchViewModel(this HumanSearchResult result) =>
        new()
        {
            UserId = result.UserId,
            BurnerName = result.BurnerName,
            ProfilePictureUrl = result.ProfilePictureUrl,
            MatchField = result.MatchField,
            MatchSnippet = result.MatchSnippet,
            MatchedEmail = result.MatchedEmail,
        };
}
