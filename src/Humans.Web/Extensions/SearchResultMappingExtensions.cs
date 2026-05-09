using Humans.Application.DTOs;
using Humans.Web.Models;

namespace Humans.Web.Extensions;

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
