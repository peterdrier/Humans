using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Services;

/// <summary>
/// Tickets' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Tickets chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. The transfer copy is hardcoded English,
/// as it was in the renderer; localizing it is tracked in peterdrier/Humans#1657
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class TicketsEmails(
    IOptions<EmailSettings> settings,
    ILogger<TicketsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>Transfer request confirmation to the sender.</summary>
    public EmailMessage TicketTransferRequested(
        string senderEmail, string senderName, string receiverName, string ticketLabel, string? culture = null)
    {
        // Kept for parity with the deleted renderer; takes effect once the copy is localized (peterdrier/Humans#1657).
        using (new CultureScope(culture, logger))
        {
            var name = Encode(senderName);
            var receiver = Encode(receiverName);
            var ticket = Encode(ticketLabel);
            return new EmailMessage(
                senderEmail, senderName,
                "Ticket transfer requested",
                $"""
                    <p>Hi {name},</p>
                    <p>We've received your request to transfer ticket <strong>{ticket}</strong> to <strong>{receiver}</strong>.</p>
                    <p>Our ticketing team will process this and let you know shortly. No further action is needed from you.</p>
                    """,
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
        // Kept for parity with the deleted renderer; takes effect once the copy is localized (peterdrier/Humans#1657).
        using (new CultureScope(culture, logger))
        {
            var name = Encode(toName);
            var receiver = Encode(receiverName);
            var ticket = Encode(ticketLabel);
            if (successful)
            {
                return new EmailMessage(
                    toEmail, toName,
                    "Ticket transfer complete",
                    $"""
                        <p>Hi {name},</p>
                        <p>The transfer of ticket <strong>{ticket}</strong> to <strong>{receiver}</strong> is complete.</p>
                        """,
                    "ticket_transfer_completed", MessageCategory.System);
            }

            var reasonHtml = string.IsNullOrWhiteSpace(reason)
                ? ""
                : $"<p><strong>Reason:</strong> {Encode(reason)}</p>";
            return new EmailMessage(
                toEmail, toName,
                "Ticket transfer cancelled",
                $"""
                    <p>Hi {name},</p>
                    <p>The requested transfer of ticket <strong>{ticket}</strong> to <strong>{receiver}</strong> was not completed.</p>
                    {reasonHtml}
                    <p>If you have questions, reply to this email and our ticketing team will help.</p>
                    """,
                "ticket_transfer_cancelled", MessageCategory.System);
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
