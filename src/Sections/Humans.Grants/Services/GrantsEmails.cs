using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Grants.Services;

/// <summary>Pure: no I/O, no persistence.</summary>
internal sealed class GrantsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<GrantsResource> localizer,
    ILogger<GrantsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage Notice(GrantNoticeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using (new CultureScope(request.Culture, logger))
        {
            var greeting = string.IsNullOrEmpty(request.RecipientName)
                ? L("Grants_Email_Greeting_Generic")
                : localizer["Grants_Email_Greeting_Named", Encode(request.RecipientName)];
            // Subjects are plain text, bodies are HTML: two forms of the same name.
            var program = request.ProgramName;
            var programHtml = Encode(request.ProgramName);
            var entitles = Encode(request.Entitles);
            var url = $"{_settings.BaseUrl.TrimEnd('/')}/{GrantService.ProfileUrl.TrimStart('/')}";
            var expiry = request.ExpiresOn is { } expires ? localizer["Grants_Email_ExpiresLine", expires.ToDate()] : "";
            var reason = string.IsNullOrEmpty(request.Reason) ? "" : localizer["Grants_Email_ReasonLine", Encode(request.Reason)];

            var (subject, body, templateName) = request.Kind switch
            {
                GrantNoticeKind.Issued => (
                    localizer["Grants_Email_Issued_Subject", program],
                    localizer["Grants_Email_Issued_Body", greeting, programHtml, entitles, expiry, url],
                    "grant_issued"),
                GrantNoticeKind.Revoked => (
                    localizer["Grants_Email_Revoked_Subject", program],
                    localizer["Grants_Email_Revoked_Body", greeting, programHtml, entitles, reason, url],
                    "grant_revoked"),
                _ => throw new InvalidOperationException($"Notice does not support kind {request.Kind}")
            };

            return new EmailMessage(request.RecipientEmail, request.RecipientName, request.RecipientUserId, subject, body,
                templateName, MessageCategory.System, Culture: CultureInfo.CurrentUICulture.Name);
        }
    }

    private string L(string key) => localizer[key].Value;

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
