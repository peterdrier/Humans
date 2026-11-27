using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Preapprovals.Services;

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
                : localizer["Preapprovals_Email_Greeting_Named", Encode(request.RecipientName)];
            // Subjects are plain text, bodies are HTML: two forms of the same name.
            var program = request.ProgramName;
            var programHtml = Encode(request.ProgramName);
            var label = string.IsNullOrEmpty(request.Label) ? "" : localizer["Preapprovals_Email_LabelLine", Encode(request.Label)];
            var amount = request.Amount.ToEuro();
            var previousAmount = request.PreviousAmount.ToEuro();
            var date = request.ClaimBy.ToDate();
            var url = $"{_settings.BaseUrl.TrimEnd('/')}/{request.Url.TrimStart('/')}";
            var detail = string.IsNullOrEmpty(request.Detail) ? "" : localizer["Preapprovals_Email_DetailLine", Encode(request.Detail)];

            var (subject, body, templateName) = request.Kind switch
            {
                PreapprovalNoticeKind.Issued => (
                    localizer["Preapprovals_Email_Issued_Subject", program],
                    localizer["Preapprovals_Email_Issued_Body", greeting, programHtml, label, amount, date, url],
                    "preapproval_issued"),
                PreapprovalNoticeKind.Declined => (
                    localizer["Preapprovals_Email_Declined_Subject", program],
                    localizer["Preapprovals_Email_Declined_Body", greeting, programHtml, detail, url],
                    "preapproval_declined"),
                PreapprovalNoticeKind.ApplicationReceived => (
                    localizer["Preapprovals_Email_ApplicationReceived_Subject", program],
                    localizer["Preapprovals_Email_ApplicationReceived_Body", greeting, programHtml, detail, amount, url],
                    "preapproval_application_received"),
                PreapprovalNoticeKind.DeadlineReminder => (
                    localizer["Preapprovals_Email_DeadlineReminder_Subject", program, date],
                    localizer["Preapprovals_Email_DeadlineReminder_Body", greeting, programHtml, label, amount, date, url],
                    "preapproval_deadline_reminder"),
                PreapprovalNoticeKind.AmountChanged => (
                    localizer["Preapprovals_Email_AmountChanged_Subject", program],
                    localizer["Preapprovals_Email_AmountChanged_Body", greeting, programHtml, label, previousAmount, amount, url],
                    "preapproval_amount_changed"),
                PreapprovalNoticeKind.Revoked => (
                    localizer["Preapprovals_Email_Revoked_Subject", program],
                    localizer["Preapprovals_Email_Revoked_Body", greeting, programHtml, label, amount, detail, url],
                    "preapproval_revoked"),
                PreapprovalNoticeKind.DeadlineChanged => (
                    localizer["Preapprovals_Email_DeadlineChanged_Subject", program, date],
                    localizer["Preapprovals_Email_DeadlineChanged_Body", greeting, programHtml, label, amount, date, url],
                    "preapproval_deadline_changed"),
                _ => throw new InvalidOperationException($"Notice does not support kind {request.Kind}")
            };

            return new EmailMessage(request.RecipientEmail, request.RecipientName, request.RecipientUserId, subject, body,
                templateName, MessageCategory.System, Culture: CultureInfo.CurrentUICulture.Name);
        }
    }

    private string L(string key) => localizer[key].Value;

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
