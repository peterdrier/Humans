using Humans.Users.Contracts;

namespace Humans.Base.Extensions;

public static class PersonSearchOrderingExtensions
{
    /// <summary>
    /// Canonical display order for person-search results: relevance score descending
    /// (exact-name &gt; prefix &gt; token-prefix &gt; contains &gt; non-name field), then
    /// BurnerName as a stable alphabetical tiebreak. The service returns matches unordered
    /// (memory/architecture/person-search.md); every people-search surface orders through here
    /// so the literal "Ian" lands above "Adrian"/"Brian" instead of one alphabetical list.
    /// </summary>
    /// <remarks>
    /// In Humans.Base rather than Shell because a section project cannot reference Humans.Web
    /// and Gate's kiosk people-picker needs the same order (design §15 step 6).
    /// </remarks>
    public static IOrderedEnumerable<HumanSearchResult> OrderByRelevance(
        this IEnumerable<HumanSearchResult> results) =>
        results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase);
}
