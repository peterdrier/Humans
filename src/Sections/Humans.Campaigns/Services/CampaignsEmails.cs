using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Campaigns.Services;

/// <summary>
/// Campaigns' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Campaigns chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Campaigns has no resx set: the subject
/// and markdown body are the campaign's own copy, carried on the request
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class CampaignsEmails
{
    public EmailMessage CampaignCode(CampaignCodeEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // HTML-encode the substitutions so malicious codes/names cannot inject markup.
        var encodedCode = Encode(request.Code);
        var encodedName = Encode(request.RecipientName);

        var markdown = request.MarkdownBody
            .Replace("{{Code}}", encodedCode, StringComparison.Ordinal)
            .Replace("{{Name}}", encodedName, StringComparison.Ordinal);

        // Subject is a plain-text field; no HTML encoding required.
        var subject = request.Subject
            .Replace("{{Code}}", request.Code, StringComparison.Ordinal)
            .Replace("{{Name}}", request.RecipientName, StringComparison.Ordinal);

        return new EmailMessage(
            request.RecipientEmail, request.RecipientName,
            subject, SanitizedMarkdownRenderer.Render(markdown),
            "campaign_code", MessageCategory.CampaignCodes,
            ReplyTo: request.ReplyTo, UserId: request.UserId, CampaignGrantId: request.CampaignGrantId,
            CampaignId: request.CampaignId);
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
