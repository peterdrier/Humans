using Humans.Users.Contracts;

namespace Humans.Users.Models;

internal sealed record FacilitatedMessageRequest(
    string RecipientEmail,
    string RecipientDisplayName,
    string SenderEmail,
    string SenderDisplayName,
    string Message,
    bool IncludeContactInfo,
    string? RecipientPreferredLanguage);

internal static class FacilitatedMessageRequestBuilder
{
    public static FacilitatedMessageRequest? TryBuild(
        UserInfo sender,
        UserInfo recipient,
        SendMessageViewModel model)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(recipient.Email) || string.IsNullOrWhiteSpace(sender.Email))
            return null;

        return new FacilitatedMessageRequest(
            recipient.Email,
            recipient.BurnerName,
            sender.Email,
            sender.BurnerName,
            model.Message,
            model.IncludeContactInfo,
            recipient.PreferredLanguage);
    }
}
