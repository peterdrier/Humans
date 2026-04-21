using Microsoft.AspNetCore.Mvc;
using Humans.Web.Controllers;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Web.Helpers;

/// <summary>
/// Resolves effective profile picture URLs for a set of users. Returns only the
/// custom-upload URL when present; otherwise the dictionary value is null and callers
/// are expected to render the initial-letter placeholder.
/// </summary>
/// <remarks>
/// Google avatar URLs (<see cref="Humans.Domain.Entities.User.ProfilePictureUrl"/>) are
/// intentionally not considered here — see issue #532. They're captured at sign-in but
/// not used for rendering, because Google restricts hotlinking and the URLs frequently
/// fail to load. Users who want their Google photo must import it via the
/// "Import my Google photo" button on <c>/Profile/Edit</c>.
/// </remarks>
public static class ProfilePictureUrlHelper
{
    /// <summary>
    /// Builds a dictionary mapping UserId → custom profile picture URL, or null when the
    /// user has no custom upload.
    /// </summary>
    public static async Task<Dictionary<Guid, string?>> BuildEffectiveUrlsAsync(
        IProfileService profileService,
        IUrlHelper urlHelper,
        IEnumerable<Guid> userIds,
        CancellationToken ct = default)
    {
        var idList = userIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        var customPictures = await profileService.GetCustomPictureInfoByUserIdsAsync(idList, ct);
        var customByUserId = customPictures.ToDictionary(
            p => p.UserId,
            p => urlHelper.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks }));

        return idList.ToDictionary(
            id => id,
            id => customByUserId.TryGetValue(id, out var customUrl) ? customUrl : null);
    }
}
