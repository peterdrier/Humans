using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Events.Services;

/// <summary>
/// Events' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Events chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. The lifecycle copy is rendered from Events resources in the recipient’s culture.
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class EventsEmails(ILogger<EventsEmails> logger, IStringLocalizer<EventsResource> localizer)
{
    public EmailMessage EventLifecycle(EventLifecycleNotification request, string userEmail)
    {
        ArgumentNullException.ThrowIfNull(request);

        using (new CultureScope(request.Culture, logger))
        {
            var userName = Encode(request.UserName);
            var eventTitle = Encode(request.EventTitle);
            var reason = Encode(request.Reason ?? string.Empty);
            var actionUrl = Encode(request.ActionUrl ?? string.Empty);

            var (subject, body) = request.NewStatus switch
            {
                EventStatus.Pending => (
                    localizer["Events_Email_SubmittedSubject"].Value,
                    localizer["Events_Email_SubmittedBody", userName, eventTitle, actionUrl].Value),
                EventStatus.Approved => (
                    localizer["Events_Email_ApprovedSubject"].Value,
                    localizer["Events_Email_ApprovedBody", userName, eventTitle].Value),
                EventStatus.Rejected => (
                    localizer["Events_Email_RejectedSubject"].Value,
                    localizer["Events_Email_RejectedBody", userName, eventTitle, reason, actionUrl].Value),
                EventStatus.ResubmitRequested => (
                    localizer["Events_Email_ResubmitRequestedSubject"].Value,
                    localizer["Events_Email_ResubmitRequestedBody", userName, eventTitle, reason, actionUrl].Value),
                _ => throw new ArgumentOutOfRangeException(nameof(request),
                    $"EventLifecycleNotification does not support status {request.NewStatus}")
            };

            return new EmailMessage(userEmail, request.UserName, subject, body, request.TemplateName());
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
