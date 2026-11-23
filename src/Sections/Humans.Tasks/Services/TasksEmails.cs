using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Tasks.Services;

/// <summary>
/// The two emails Tasks sends: a manager's answer to a request. Pure: no I/O. Copy renders in the recipient's culture
/// inside a <see cref="CultureScope"/> (memory/architecture/email-templates-live-in-sender.md).
/// </summary>
internal sealed class TasksEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<TasksResource> localizer,
    ILogger<TasksEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage RequestApproved(TaskAnswerEmail request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Localized(request.Culture, () => new EmailMessage(
            request.RecipientEmail, request.RecipientName, request.RecipientUserId,
            Lf("Tasks_Email_RequestApproved_Subject", request.TaskTitle),
            Lf("Tasks_Email_RequestApproved_Body",
                Greeting(request.RecipientName), Encode(request.TaskTitle), Encode(request.TeamName), TaskUrl(request)),
            "task_request_approved", MessageCategory.VolunteerUpdates));
    }

    public EmailMessage RequestDeclined(TaskAnswerEmail request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Localized(request.Culture, () => new EmailMessage(
            request.RecipientEmail, request.RecipientName, request.RecipientUserId,
            Lf("Tasks_Email_RequestDeclined_Subject", request.TaskTitle),
            Lf("Tasks_Email_RequestDeclined_Body",
                Greeting(request.RecipientName), Encode(request.TaskTitle), Encode(request.TeamName),
                ReasonLine(request.Reason), TaskUrl(request)),
            "task_request_declined", MessageCategory.VolunteerUpdates));
    }

    private string Greeting(string recipientName) => Lf("Tasks_Email_Greeting", Encode(recipientName));

    /// <summary>Nothing when the manager gave no reason.</summary>
    private string ReasonLine(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : Lf("Tasks_Email_ReasonLine", Encode(reason));

    private string TaskUrl(TaskAnswerEmail request) => $"{_settings.BaseUrl}/Tasks/{request.TaskId}";

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build() with { Culture = CultureInfo.CurrentUICulture.Name };
        }
    }
}

/// <summary>
/// What an answer email is built from. <paramref name="Reason"/> is a decline's optional free text and is never stored.
/// </summary>
internal sealed record TaskAnswerEmail(
    Guid RecipientUserId,
    string RecipientEmail,
    string RecipientName,
    Guid TaskId,
    string TaskTitle,
    string TeamName,
    string? Reason = null,
    string? Culture = null);
