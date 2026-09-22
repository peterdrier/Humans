using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Issues.Services;

/// <summary>
/// Issues' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Issues chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Issues' own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class IssuesEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<IssuesResource> localizer,
    ILogger<IssuesEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage IssueComment(string to, string displayName, string issueTitle, string commentContent, string issueLink, string preferredLanguage)
        => Localized(preferredLanguage, () => new EmailMessage(
            to, displayName,
            Lf("Issues_Email_IssueComment_Subject", issueTitle),
            Lf("Issues_Email_IssueComment_Body", Encode(displayName), Encode(issueTitle),
                SanitizedMarkdownRenderer.Render(commentContent), Encode(AbsoluteUrl(issueLink))),
            "issue_comment", MessageCategory.System));

    /// <summary>
    /// Webmail has no Humans origin, so a root-relative path in an email resolves against
    /// the reader's own host; an already-absolute link is passed through unchanged.
    /// </summary>
    private string AbsoluteUrl(string issueLink) =>
        issueLink.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? issueLink
            : $"{_settings.BaseUrl.TrimEnd('/')}{(issueLink.StartsWith('/') ? "" : "/")}{issueLink}";

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build();
        }
    }
}
