using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Events.Services;

/// <summary>Rendered in the recipient's culture. Pure: no I/O.</summary>
internal sealed class EventsEmails(
    IStringLocalizer<EventsResource> localizer,
    ILogger<EventsEmails> logger)
{
    public EmailMessage EventLifecycle(Guid userId, EventLifecycleNotification request, string userEmail)
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
                userEmail, request.UserName, userId, localizer[key + "_Subject"].Value, body, request.TemplateName());
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
