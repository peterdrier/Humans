using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Events.Services;

/// <summary>
/// Events' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Events chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Events' own resx set and is
/// rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class EventsEmails(
    IStringLocalizer<EventsResource> localizer,
    ILogger<EventsEmails> logger)
{
    public EmailMessage EventLifecycle(EventLifecycleNotification request, string userEmail)
    {
        ArgumentNullException.ThrowIfNull(request);

        using (new CultureScope(request.Culture, logger))
        {
            var key = request.NewStatus switch
            {
                EventStatus.Pending => "Events_Email_Pending",
                EventStatus.Approved => "Events_Email_Approved",
                EventStatus.Rejected => "Events_Email_Rejected",
                EventStatus.ResubmitRequested => "Events_Email_ResubmitRequested",
                _ => throw new ArgumentOutOfRangeException(nameof(request),
                    $"EventLifecycleNotification does not support status {request.NewStatus}")
            };

            var body = string.Format(CultureInfo.CurrentCulture, localizer[key + "_Body"].Value,
                Encode(request.UserName),
                Encode(request.EventTitle),
                Encode(request.Reason ?? string.Empty),
                Encode(request.ActionUrl ?? string.Empty));

            return new EmailMessage(
                userEmail, request.UserName, localizer[key + "_Subject"].Value, body, request.TemplateName());
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
