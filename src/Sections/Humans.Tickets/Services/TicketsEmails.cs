using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Services;

/// <summary>
/// Tickets' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Tickets chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. The member-facing copy lives in Tickets' own
/// resx set and is rendered in the recipient's culture inside a <see cref="CultureScope"/>; the
/// ticket-team notice stays English (memory/architecture/email-templates-live-in-sender.md,
/// peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class TicketsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<TicketsResource> localizer,
    ILogger<TicketsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>Transfer request confirmation to the sender.</summary>
    public EmailMessage TicketTransferRequested(
        string senderEmail, string senderName, string receiverName, string ticketLabel, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            return new EmailMessage(
                senderEmail, senderName,
                localizer["Tickets_TicketTransfer_Email_Requested_Subject"].Value,
                Lf("Tickets_TicketTransfer_Email_Requested_Body", Encode(senderName), Encode(ticketLabel), Encode(receiverName)),
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
            var ticket = Encode(ticketLabel);
            var receiver = Encode(receiverName);
            if (successful)
            {
                return new EmailMessage(
                    toEmail, toName,
                    localizer["Tickets_TicketTransfer_Email_Completed_Subject"].Value,
                    Lf("Tickets_TicketTransfer_Email_Completed_Body", name, ticket, receiver),
                    "ticket_transfer_completed", MessageCategory.System);
            }

            var reasonHtml = string.IsNullOrWhiteSpace(reason)
                ? ""
                : Lf("Tickets_TicketTransfer_Email_Cancelled_Reason", Encode(reason));
            return new EmailMessage(
                toEmail, toName,
                localizer["Tickets_TicketTransfer_Email_Cancelled_Subject"].Value,
                Lf("Tickets_TicketTransfer_Email_Cancelled_Body", name, ticket, receiver, reasonHtml),
                "ticket_transfer_cancelled", MessageCategory.System);
        }
    }

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
