using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Preapprovals.Services;

/// <summary>
/// Preapprovals' own email template: one method returning a ready <see cref="EmailMessage"/>
/// for the single <see cref="IEmailService.SendAsync"/> path. Copy lives in the section's own
/// resx set and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md). Pure — no I/O, no persistence.
/// </summary>
internal sealed class PreapprovalsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<PreapprovalsResource> localizer,
    ILogger<PreapprovalsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage Notice(PreapprovalNoticeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using (new CultureScope(request.Culture, logger))
        {
            var greeting = string.IsNullOrEmpty(request.RecipientName)
                ? L("Preapprovals_Email_Greeting_Generic")
                : Lf("Preapprovals_Email_Greeting_Named", Encode(request.RecipientName));
            // Subjects are plain text, bodies are HTML: two forms of the same name.
            var program = request.ProgramName;
            var programHtml = Encode(request.ProgramName);
            var label = string.IsNullOrEmpty(request.Label) ? "" : Lf("Preapprovals_Email_LabelLine", Encode(request.Label));
            var amount = Euros(request.Amount);
            var date = request.ClaimBy.ToDate();
            var url = $"{_settings.BaseUrl.TrimEnd('/')}/{request.Url.TrimStart('/')}";
            var detail = string.IsNullOrEmpty(request.Detail) ? "" : Lf("Preapprovals_Email_DetailLine", Encode(request.Detail));

            var (subject, body, templateName) = request.Kind switch
            {
                PreapprovalNoticeKind.Issued => (
                    Lf("Preapprovals_Email_Issued_Subject", program),
                    Lf("Preapprovals_Email_Issued_Body", greeting, programHtml, label, amount, date, url),
                    "preapproval_issued"),
                PreapprovalNoticeKind.Declined => (
                    Lf("Preapprovals_Email_Declined_Subject", program),
                    Lf("Preapprovals_Email_Declined_Body", greeting, programHtml, detail, url),
                    "preapproval_declined"),
                PreapprovalNoticeKind.ApplicationReceived => (
                    Lf("Preapprovals_Email_ApplicationReceived_Subject", program),
                    Lf("Preapprovals_Email_ApplicationReceived_Body", greeting, programHtml, detail, amount, url),
                    "preapproval_application_received"),
                PreapprovalNoticeKind.DeadlineReminder => (
                    Lf("Preapprovals_Email_DeadlineReminder_Subject", program, date),
                    Lf("Preapprovals_Email_DeadlineReminder_Body", greeting, programHtml, label, amount, date, url),
                    "preapproval_deadline_reminder"),
                PreapprovalNoticeKind.AmountChanged => (
                    Lf("Preapprovals_Email_AmountChanged_Subject", program),
                    Lf("Preapprovals_Email_AmountChanged_Body", greeting, programHtml, label, detail, amount, url),
                    "preapproval_amount_changed"),
                PreapprovalNoticeKind.Revoked => (
                    Lf("Preapprovals_Email_Revoked_Subject", program),
                    Lf("Preapprovals_Email_Revoked_Body", greeting, programHtml, label, amount, detail, url),
                    "preapproval_revoked"),
                PreapprovalNoticeKind.DeadlineChanged => (
                    Lf("Preapprovals_Email_DeadlineChanged_Subject", program, date),
                    Lf("Preapprovals_Email_DeadlineChanged_Body", greeting, programHtml, label, amount, date, url),
                    "preapproval_deadline_changed"),
                _ => throw new InvalidOperationException($"Notice does not support kind {request.Kind}")
            };

            return new EmailMessage(request.RecipientEmail, request.RecipientName, subject, body,
                templateName, MessageCategory.System, Culture: CultureInfo.CurrentUICulture.Name);
        }
    }

    /// <summary>Amount in the recipient's own number format — "1.234,56 €" in es, "1,234.56 €" in en.</summary>
    private static string Euros(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture) + " €";

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
