using System.Globalization;
using Humans.Base.Extensions;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Models;

internal sealed record DeletionRequestFlash(bool Success, string ResourceKey, Instant? EffectiveDeletionDate)
{
    public static DeletionRequestFlash From(DeletionRequestResult result)
    {
        if (!result.Success)
            return new(false, ErrorResourceKeyFor(result.ErrorKey), null);

        return new(
            true,
            result.IsHeldForTicket ? "Gdpr_Deletion_HeldForTicket" : "Gdpr_Deletion_Requested",
            result.EffectiveDeletionDate);
    }

    public string Format(IStringLocalizer<GdprResource> localizer) =>
        string.Format(CultureInfo.CurrentCulture, localizer[ResourceKey].Value, EffectiveDeletionDate.ToDate());

    private static string ErrorResourceKeyFor(string? errorKey) =>
        string.Equals(errorKey, "AlreadyPending", StringComparison.Ordinal)
            ? "Gdpr_Deletion_AlreadyPending"
            : "Gdpr_Deletion_RequestFailed";
}
