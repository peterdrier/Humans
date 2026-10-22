using System.Net;
using System.Text.RegularExpressions;
using Humans.Base.Extensions;
using Humans.Campaigns.Domain;
using Humans.Email.Contracts;

namespace Humans.Campaigns.Services;

/// <summary>Pure: no I/O, no persistence.</summary>
internal sealed class CampaignsEmails
{
    public EmailMessage CampaignCode(CampaignCodeEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Keep values out of the Markdown parser, including code spans and link destinations.
        // Restore HTML-encoded data before the shared sanitizer checks the completed HTML/URLs.
        var encodedCode = Encode(request.Code);
        var encodedName = Encode(request.RecipientName);
        var marker = "humans" + Guid.NewGuid().ToString("N");
        var codeMarker = marker + "code";
        var nameMarker = marker + "name";
        var markdown = Substitute(request.MarkdownBody, codeMarker, nameMarker);
        var html = SanitizedMarkdownRenderer.Render(markdown,
            transformHtml: rendered => Substitute(rendered, encodedCode, encodedName, codeMarker, nameMarker));

        var subject = Substitute(request.Subject, request.Code, request.RecipientName);
        var subjectRunes = subject.EnumerateRunes().Take(Campaign.EmailSubjectMaxLength + 1).ToArray();
        if (subjectRunes.Length > Campaign.EmailSubjectMaxLength)
        {
            // Expansion can exceed the stored template's bound. Keep the complete subject
            // in the body, including codes absent from the authored body, and shorten only the header.
            html = "<p><strong>" + Encode(subject) + "</strong></p>\n" + html;
            subject = string.Concat(subjectRunes.Take(Campaign.EmailSubjectMaxLength - 1).Select(rune => rune.ToString())) + "…";
        }

        return new EmailMessage(
            request.RecipientEmail, request.RecipientName, request.UserId,
            subject, html,
            "campaign_code", MessageCategory.CampaignCodes,
            ReplyTo: request.ReplyTo,
            CampaignId: request.CampaignId);
    }

    // Match only the original template: inserted codes and names are literal values.
    private static string Substitute(string template, string code, string name,
        string codePlaceholder = "{{Code}}", string namePlaceholder = "{{Name}}") =>
        Regex.Replace(template, Regex.Escape(codePlaceholder) + "|" + Regex.Escape(namePlaceholder),
            match => string.Equals(match.Value, codePlaceholder, StringComparison.Ordinal) ? code : name,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
