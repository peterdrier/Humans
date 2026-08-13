using Humans.Application.DTOs;
using Humans.UI.Models;
using Humans.Users.Contracts;

namespace Humans.UI.Extensions;

/// <summary>
/// Projects a person-search hit onto the row shape the canonical
/// <c>_HumanSearchResults</c> partial binds. In <c>Humans.UI</c> beside
/// <see cref="PersonSearchOrderingExtensions"/> because both of its callers —
/// Shell's <c>ProfileController</c> and the Search section's
/// <c>SearchController</c> — need it, and a section cannot name a
/// <c>Humans.Web</c> type.
/// </summary>
public static class SearchResultMappingExtensions
{
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
