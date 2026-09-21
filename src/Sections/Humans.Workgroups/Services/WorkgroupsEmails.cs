using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Workgroups.Services;

/// <summary>
/// Workgroups' own email template: one method returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Workgroups chooses —
/// template name per notice kind and the Governance opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Workgroups' own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class WorkgroupsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<WorkgroupsResource> localizer,
    ILogger<WorkgroupsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage WorkgroupNotice(WorkgroupNoticeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using (new CultureScope(request.Culture, logger))
        {
            var greeting = string.IsNullOrEmpty(request.RecipientName)
                ? L("Workgroups_Email_WorkgroupNotice_Greeting_Generic")
                : Lf("Workgroups_Email_WorkgroupNotice_Greeting_Named", Encode(request.RecipientName));
            // Two forms of the same name: subjects are plain text, bodies are HTML. Encoding
            // once for both would put "Health &amp; Safety" in the subject line.
            var name = request.WorkgroupName;
            var nameHtml = Encode(request.WorkgroupName);
            var url = $"{_settings.BaseUrl}/Workgroups/{request.WorkgroupSlug}";
            var detail = Encode(request.Detail ?? "");

            // The reasons line is omitted entirely rather than rendered empty.
            string ReasonLine() => string.IsNullOrEmpty(request.Detail)
                ? ""
                : Lf("Workgroups_Email_ReasonLine", detail);

            var (subject, body, templateName) = request.Kind switch
            {
                WorkgroupNoticeKind.Applied => (
                    Lf("Workgroups_Email_WorkgroupNotice_Applied_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Applied_Body", greeting, nameHtml, url),
                    "workgroup_notice_applied"),
                WorkgroupNoticeKind.Referred => (
                    Lf("Workgroups_Email_WorkgroupNotice_Referred_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Referred_Body", greeting, nameHtml, url),
                    "workgroup_notice_referred"),
                WorkgroupNoticeKind.Registered => (
                    Lf("Workgroups_Email_WorkgroupNotice_Registered_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Registered_Body", greeting, nameHtml, url),
                    "workgroup_notice_registered"),
                WorkgroupNoticeKind.Refused => (
                    Lf("Workgroups_Email_WorkgroupNotice_Refused_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Refused_Body", greeting, nameHtml, ReasonLine(), url),
                    "workgroup_notice_refused"),
                WorkgroupNoticeKind.Withdrawn => (
                    Lf("Workgroups_Email_WorkgroupNotice_Withdrawn_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Withdrawn_Body", greeting, nameHtml, ReasonLine(), url),
                    "workgroup_notice_withdrawn"),
                WorkgroupNoticeKind.Ended => (
                    Lf("Workgroups_Email_WorkgroupNotice_Ended_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Ended_Body", greeting, nameHtml, url),
                    "workgroup_notice_ended"),
                WorkgroupNoticeKind.Reactivated => (
                    Lf("Workgroups_Email_WorkgroupNotice_Reactivated_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Reactivated_Body", greeting, nameHtml, url),
                    "workgroup_notice_reactivated"),
                WorkgroupNoticeKind.CoordinatorsChanged => (
                    Lf("Workgroups_Email_WorkgroupNotice_CoordinatorsChanged_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_CoordinatorsChanged_Body", greeting, nameHtml, url),
                    "workgroup_notice_coordinators_changed"),
                WorkgroupNoticeKind.DormancyInquiry => (
                    Lf("Workgroups_Email_WorkgroupNotice_DormancyInquiry_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_DormancyInquiry_Body", greeting, nameHtml, detail, url),
                    "workgroup_notice_dormancy_inquiry"),
                WorkgroupNoticeKind.Delivered => (
                    Lf("Workgroups_Email_WorkgroupNotice_Delivered_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_Delivered_Body", greeting, nameHtml, detail, url),
                    "workgroup_notice_delivered"),
                WorkgroupNoticeKind.DispositionRecorded => (
                    Lf("Workgroups_Email_WorkgroupNotice_DispositionRecorded_Subject", name),
                    Lf("Workgroups_Email_WorkgroupNotice_DispositionRecorded_Body", greeting, nameHtml, detail, url),
                    "workgroup_notice_disposition_recorded"),
                _ => throw new InvalidOperationException(
                    $"WorkgroupNotice does not support kind {request.Kind}")
            };

            return new EmailMessage(request.RecipientEmail, request.RecipientName, subject, body,
                templateName, MessageCategory.Governance);
        }
    }

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
