using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Models;

internal sealed record GuestDeletionRequestFlash(bool Success, string ResourceKey, Instant? EffectiveDeletionDate)
{
    public static GuestDeletionRequestFlash From(DeletionRequestResult result)
    {
        if (!result.Success)
            return new(false, ErrorResourceKeyFor(result.ErrorKey), null);

        return new(
            true,
            result.IsHeldForTicket ? "Users_Guest_DeletionHeldForTicket" : "Users_Guest_DeletionRequested",
            result.EffectiveDeletionDate);
    }

    private static string ErrorResourceKeyFor(string? errorKey) =>
        string.Equals(errorKey, "AlreadyPending", StringComparison.Ordinal)
            ? "Profile_DeletionAlreadyPending"
            : "Users_Guest_DeletionRequestFailed";
}
