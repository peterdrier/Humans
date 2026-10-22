using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Issues.Services;

/// <summary>Rendered in the recipient's culture. Pure: no I/O.</summary>
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
    /// Webmail has no Humans origin, so a root-relative link would resolve against the reader's host. Absolute links
    /// pass through.
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
