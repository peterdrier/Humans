using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;

namespace Humans.Tickets.Services;

/// <summary>
/// Tickets' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Tickets chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. The member transfer copy is rendered from Tickets resources in the recipient’s culture.
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class TicketsEmails(
    IOptions<EmailSettings> settings,
    ILogger<TicketsEmails> logger,
    IStringLocalizer<TicketsResource> localizer)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>Transfer request confirmation to the sender.</summary>
    public EmailMessage TicketTransferRequested(
        string senderEmail, string senderName, string receiverName, string ticketLabel, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var name = Encode(senderName);
            var receiver = Encode(receiverName);
            var ticket = Encode(ticketLabel);
            return new EmailMessage(
                senderEmail, senderName,
                localizer["Tickets_Email_TransferRequestedSubject"].Value,
                localizer["Tickets_Email_TransferRequestedBody", name, ticket, receiver].Value,
                "ticket_transfer_requested", MessageCategory.System);
        }
    }

    /// <summary>Action-needed notice to the ticket team inbox. Always English — an operator mailbox.</summary>
    public EmailMessage TicketTransferTeamNotification(
        string senderName, string receiverName, string receiverEmail,
        string ticketLabel, string? reason, string reviewUrl)
    {
        var sender = Encode(senderName);
        var receiver = Encode(receiverName);
        var email = Encode(receiverEmail);
        var ticket = Encode(ticketLabel);
        var reasonHtml = string.IsNullOrWhiteSpace(reason)
            ? ""
            : $"<p><strong>Reason given:</strong> {Encode(reason)}</p>";
        var fullUrl = reviewUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? reviewUrl
            : $"{_settings.BaseUrl.TrimEnd('/')}{(reviewUrl.StartsWith('/') ? "" : "/")}{reviewUrl}";
        return new EmailMessage(
            TicketConstants.TicketsTeamEmail, "Ticket team",
            "Ticket transfer to process",
            $"""
                <p>A new ticket transfer is awaiting manual processing in TicketTailor.</p>
                <p><strong>From:</strong> {sender}<br>
                <strong>To:</strong> {receiver} &lt;{email}&gt;<br>
                <strong>Ticket:</strong> {ticket}</p>
                {reasonHtml}
                <p>Void the original and reissue to the recipient in TicketTailor, then mark the request
                resolved here: <a href="{Encode(fullUrl)}">Review transfer</a></p>
                """,
            "ticket_transfer_team", MessageCategory.System);
    }

    /// <summary>Transfer outcome (completed or cancelled) to the sender and the receiver.</summary>
    public EmailMessage TicketTransferDecision(
        string toEmail, string toName, bool successful, string ticketLabel, string receiverName,
        string? reason, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var name = Encode(toName);
            var receiver = Encode(receiverName);
            var ticket = Encode(ticketLabel);
            if (successful)
            {
                return new EmailMessage(
                    toEmail, toName,
                    localizer["Tickets_Email_TransferCompletedSubject"].Value,
                    localizer["Tickets_Email_TransferCompletedBody", name, ticket, receiver].Value,
                    "ticket_transfer_completed", MessageCategory.System);
            }

            var reasonHtml = string.IsNullOrWhiteSpace(reason)
                ? ""
                : localizer["Tickets_Email_TransferCancellationReason", Encode(reason)].Value;
            return new EmailMessage(
                toEmail, toName,
                localizer["Tickets_Email_TransferCancelledSubject"].Value,
                localizer["Tickets_Email_TransferCancelledBody", name, ticket, receiver, reasonHtml].Value,
                "ticket_transfer_cancelled", MessageCategory.System);
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
