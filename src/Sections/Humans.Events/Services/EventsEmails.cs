using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Users.Contracts;

namespace Humans.Events.Services;

/// <summary>
/// Events' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Events chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. The lifecycle copy is hardcoded English,
/// as it was in the renderer; localizing it is tracked in peterdrier/Humans#1657
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class EventsEmails(ILogger<EventsEmails> logger)
{
    public EmailMessage EventLifecycle(EventLifecycleNotification request, string userEmail)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Kept for parity with the deleted renderer; takes effect once the copy is localized (peterdrier/Humans#1657).
        using (new CultureScope(request.Culture, logger))
        {
            var userName = Encode(request.UserName);
            var eventTitle = Encode(request.EventTitle);
            var reason = Encode(request.Reason ?? string.Empty);
            var actionUrl = Encode(request.ActionUrl ?? string.Empty);

            var (subject, body) = request.NewStatus switch
            {
                EventStatus.Pending => (
                    "Your event submission has been received",
                    $"""
                        <p>Hi {userName},</p>
                        <p>Your event <strong>{eventTitle}</strong> has been received and is now in the moderation queue.
                        You will be notified once it has been reviewed.</p>
                        <p><a href="{actionUrl}">View your submissions</a></p>
                        """),
                EventStatus.Approved => (
                    "Your event has been approved",
                    $"""
                        <p>Hi {userName},</p>
                        <p>Your event <strong>{eventTitle}</strong> has been approved and will appear in the event guide.</p>
                        """),
                EventStatus.Rejected => (
                    "Your event submission was not approved",
                    $"""
                        <p>Hi {userName},</p>
                        <p>Your event <strong>{eventTitle}</strong> was not approved for the event guide.</p>
                        <p><strong>Reason:</strong> {reason}</p>
                        <p>You can edit and resubmit your event here: <a href="{actionUrl}">Edit event</a></p>
                        """),
                EventStatus.ResubmitRequested => (
                    "Changes requested for your event submission",
                    $"""
                        <p>Hi {userName},</p>
                        <p>The moderation team has requested changes to your event <strong>{eventTitle}</strong> before it can be approved.</p>
                        <p><strong>Feedback:</strong> {reason}</p>
                        <p>Please update and resubmit here: <a href="{actionUrl}">Edit event</a></p>
                        """),
                _ => throw new ArgumentOutOfRangeException(nameof(request),
                    $"EventLifecycleNotification does not support status {request.NewStatus}")
            };

            return new EmailMessage(userEmail, request.UserName, subject, body, request.TemplateName());
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
