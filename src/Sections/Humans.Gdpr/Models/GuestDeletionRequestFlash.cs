using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Gdpr.Models;

internal sealed record GuestDeletionRequestFlash(bool Success, string ResourceKey, Instant? EffectiveDeletionDate)
{
    public static GuestDeletionRequestFlash From(DeletionRequestResult result)
    {
        if (!result.Success)
            return new(false, ErrorResourceKeyFor(result.ErrorKey), null);

        return new(
            true,
            result.IsHeldForTicket ? "Gdpr_Guest_DeletionHeldForTicket" : "Gdpr_Guest_DeletionRequested",
            result.EffectiveDeletionDate);
    }

    private static string ErrorResourceKeyFor(string? errorKey) =>
        string.Equals(errorKey, "AlreadyPending", StringComparison.Ordinal)
            ? "Gdpr_Deletion_AlreadyPending"
            : "Gdpr_Guest_DeletionRequestFailed";
}
