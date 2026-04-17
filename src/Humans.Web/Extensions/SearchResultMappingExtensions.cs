using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Extensions;

public static class SearchResultMappingExtensions
{
    public static HumanSearchResultViewModel ToHumanSearchViewModel(this HumanSearchResult result, IUrlHelper url) =>
        new()
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
            MatchField = result.MatchField,
            MatchSnippet = result.MatchSnippet
        };

    public static HumanLookupSearchResult ToHumanLookupSearchResult(this HumanSearchResult result) =>
        new(result.UserId, result.DisplayName, result.BurnerName);

    public static ApprovedUserSearchResult ToApprovedUserSearchResult(this UserSearchResult result) =>
        new(result.UserId, result.DisplayName, result.Email);
}
