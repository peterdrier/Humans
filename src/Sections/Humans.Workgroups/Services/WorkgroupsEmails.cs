using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Workgroups.Services;

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
                : localizer["Workgroups_Email_WorkgroupNotice_Greeting_Named", Encode(request.RecipientName)];
            // Subjects are plain text, bodies HTML: encoding once for both would put "Health &amp; Safety" in the subject.
            var name = request.WorkgroupName;
            var nameHtml = Encode(request.WorkgroupName);
            var url = $"{_settings.BaseUrl.TrimEnd('/')}/Workgroups/{request.WorkgroupSlug}";
            var detail = Encode(request.Detail ?? "");

            string ReasonLine() => string.IsNullOrEmpty(request.Detail)
                ? ""
                : localizer["Workgroups_Email_ReasonLine", detail];

            var (subject, body, templateName) = request.Kind switch
            {
                WorkgroupNoticeKind.Applied => (
                    localizer["Workgroups_Email_WorkgroupNotice_Applied_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Applied_Body", greeting, nameHtml, url],
                    "workgroup_notice_applied"),
                WorkgroupNoticeKind.Referred => (
                    localizer["Workgroups_Email_WorkgroupNotice_Referred_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Referred_Body", greeting, nameHtml, url],
                    "workgroup_notice_referred"),
                WorkgroupNoticeKind.Registered => (
                    localizer["Workgroups_Email_WorkgroupNotice_Registered_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Registered_Body", greeting, nameHtml, url],
                    "workgroup_notice_registered"),
                WorkgroupNoticeKind.Refused => (
                    localizer["Workgroups_Email_WorkgroupNotice_Refused_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Refused_Body", greeting, nameHtml, ReasonLine(), url],
                    "workgroup_notice_refused"),
                WorkgroupNoticeKind.Withdrawn => (
                    localizer["Workgroups_Email_WorkgroupNotice_Withdrawn_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Withdrawn_Body", greeting, nameHtml, ReasonLine(), url],
                    "workgroup_notice_withdrawn"),
                WorkgroupNoticeKind.Ended => (
                    localizer["Workgroups_Email_WorkgroupNotice_Ended_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Ended_Body", greeting, nameHtml, url],
                    "workgroup_notice_ended"),
                WorkgroupNoticeKind.Reactivated => (
                    localizer["Workgroups_Email_WorkgroupNotice_Reactivated_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Reactivated_Body", greeting, nameHtml, url],
                    "workgroup_notice_reactivated"),
                WorkgroupNoticeKind.CoordinatorsChanged => (
                    localizer["Workgroups_Email_WorkgroupNotice_CoordinatorsChanged_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_CoordinatorsChanged_Body", greeting, nameHtml, url],
                    "workgroup_notice_coordinators_changed"),
                WorkgroupNoticeKind.DormancyInquiry => (
                    localizer["Workgroups_Email_WorkgroupNotice_DormancyInquiry_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_DormancyInquiry_Body", greeting, nameHtml, detail, url],
                    "workgroup_notice_dormancy_inquiry"),
                WorkgroupNoticeKind.Delivered => (
                    localizer["Workgroups_Email_WorkgroupNotice_Delivered_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_Delivered_Body", greeting, nameHtml, detail, url],
                    "workgroup_notice_delivered"),
                WorkgroupNoticeKind.DispositionRecorded => (
                    localizer["Workgroups_Email_WorkgroupNotice_DispositionRecorded_Subject", name],
                    localizer["Workgroups_Email_WorkgroupNotice_DispositionRecorded_Body", greeting, nameHtml, detail, url],
                    "workgroup_notice_disposition_recorded"),
                _ => throw new InvalidOperationException(
                    $"WorkgroupNotice does not support kind {request.Kind}")
            };

            return new EmailMessage(request.RecipientEmail, request.RecipientName, request.RecipientUserId, subject, body,
                templateName, MessageCategory.Governance, Culture: CultureInfo.CurrentUICulture.Name);
        }
    }

    private string L(string key) => localizer[key].Value;

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
