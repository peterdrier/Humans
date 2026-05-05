using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Services.Profile;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Extensions;

public static class SearchResultMappingExtensions
{
    /// <summary>
    /// Maps a <see cref="HumanSearchResult"/> from <c>SearchProfilesAsync</c> to
    /// the broad-search view model, computing <see cref="HumanSearchResultViewModel.MatchField"/>
    /// and <see cref="HumanSearchResultViewModel.MatchSnippet"/> from the result fields
    /// using the original query string.
    /// </summary>
    public static HumanSearchResultViewModel ToHumanSearchViewModel(
        this HumanSearchResult result, IUrlHelper url, string query)
    {
        var (matchField, matchSnippet) = HumanSearchMatcher.DetermineMatch(result, query);
        return new HumanSearchResultViewModel
        {
            UserId = result.UserId,
            DisplayName = result.DisplayName,
            BurnerName = result.BurnerName,
            City = result.City,
            Bio = result.Bio,
            ContributionInterests = result.ContributionInterests,
            EffectiveProfilePictureUrl = result.HasCustomPicture
                ? url.Action(nameof(ProfileController.Picture), "Profile", new { id = result.ProfileId, v = result.UpdatedAtTicks })
                : result.ProfilePictureUrl,
            MatchField = matchField,
            MatchSnippet = matchSnippet
        };
    }

    public static HumanLookupSearchResult ToHumanLookupSearchResult(this HumanSearchResult result) =>
        new(result.UserId, result.DisplayName, result.BurnerName);

    public static ApprovedUserSearchResult ToApprovedUserSearchResult(this HumanSearchResult result) =>
        new(result.UserId, result.DisplayName, result.PrimaryEmail ?? "");
}
