using Humans.Application.Interfaces.Users;
using Humans.Web.Extensions;

namespace Humans.Web.Models;

internal sealed record GuestDeletionRequestFlash(bool Success, string Message)
{
    public static GuestDeletionRequestFlash From(DeletionRequestResult result)
    {
        if (!result.Success)
            return new(false, ErrorMessageFor(result.ErrorKey));

        var effective = result.EffectiveDeletionDate.ToDate();
        var message = result.IsHeldForTicket
            ? $"Deletion request recorded. Because you have tickets for an upcoming event, your account will be deleted after {effective}."
            : $"Deletion request recorded. Your account will be permanently deleted on {effective}.";

        return new(true, message);
    }

    private static string ErrorMessageFor(string? errorKey) =>
        string.Equals(errorKey, "AlreadyPending", StringComparison.Ordinal)
            ? "A deletion request is already pending."
            : "Failed to process deletion request. Please try again.";
}
