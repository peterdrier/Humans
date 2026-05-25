using Humans.Application;

namespace Humans.Web.Models;

public sealed record FacilitatedMessageRequest(
    string RecipientEmail,
    string RecipientDisplayName,
    string SenderEmail,
    string SenderDisplayName,
    string CleanMessage,
    bool IncludeContactInfo,
    string? RecipientPreferredLanguage);

public static class FacilitatedMessageRequestBuilder
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

        var cleanMessage = System.Text.RegularExpressions.Regex.Replace(
            model.Message,
            "<[^>]+>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

        return new FacilitatedMessageRequest(
            recipient.Email,
            recipient.BurnerName,
            sender.Email,
            sender.BurnerName,
            cleanMessage,
            model.IncludeContactInfo,
            recipient.PreferredLanguage);
    }
}
