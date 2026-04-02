using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Web.Controllers;

namespace Humans.Web.Helpers;

/// <summary>
/// Resolves effective profile picture URLs (custom upload takes priority over Google avatar).
/// Centralizes the pattern used across Map, Birthdays, Team Details, etc.
/// </summary>
public static class ProfilePictureUrlHelper
{
    /// <summary>
    /// Builds a dictionary mapping UserId → effective profile picture URL
    /// for a set of users, resolving custom uploads vs Google avatars.
    /// </summary>
    public static async Task<Dictionary<Guid, string?>> BuildEffectiveUrlsAsync(
        IProfileService profileService,
        IUrlHelper urlHelper,
        IEnumerable<(Guid UserId, string? GoogleProfilePictureUrl)> users,
        CancellationToken ct = default)
    {
        var userList = users.DistinctBy(u => u.UserId).ToList();
        var userIds = userList.Select(u => u.UserId).ToList();

        var customPictures = await profileService.GetCustomPictureInfoByUserIdsAsync(userIds, ct);
        var customByUserId = customPictures.ToDictionary(
            p => p.UserId,
            p => urlHelper.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks }));

        return userList.ToDictionary(
            u => u.UserId,
            u => customByUserId.TryGetValue(u.UserId, out var customUrl) ? customUrl : u.GoogleProfilePictureUrl);
    }
}
