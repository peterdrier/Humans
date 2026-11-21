using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Services;

/// <summary>Member-facing copy renders in the recipient's culture; the ticket-team notice stays English.</summary>
internal sealed class TicketsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<TicketsResource> localizer,
    ILogger<TicketsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage TicketTransferRequested(
        Guid senderUserId, string senderEmail, string senderName, string receiverName, string ticketLabel, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            return new EmailMessage(
                senderEmail, senderName, senderUserId,
                localizer["Tickets_TicketTransfer_Email_Requested_Subject"].Value,
                Lf("Tickets_TicketTransfer_Email_Requested_Body", Encode(senderName), Encode(ticketLabel), Encode(receiverName)),
                "ticket_transfer_requested", MessageCategory.System);
        }
    }

    /// <summary>Always English, and to no user: an operator mailbox.</summary>
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
            TicketConstants.TicketsTeamEmail, "Ticket team", UserId: null,
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

    public EmailMessage TicketTransferDecision(
        Guid toUserId, string toEmail, string toName, bool successful, string ticketLabel, string receiverName,
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
                    toEmail, toName, toUserId,
                    localizer["Tickets_TicketTransfer_Email_Completed_Subject"].Value,
                    Lf("Tickets_TicketTransfer_Email_Completed_Body", name, ticket, receiver),
                    "ticket_transfer_completed", MessageCategory.System);
            }

            var reasonHtml = string.IsNullOrWhiteSpace(reason)
                ? ""
                : Lf("Tickets_TicketTransfer_Email_Cancelled_Reason", Encode(reason));
            return new EmailMessage(
                toEmail, toName, toUserId,
                localizer["Tickets_TicketTransfer_Email_Cancelled_Subject"].Value,
                Lf("Tickets_TicketTransfer_Email_Cancelled_Body", name, ticket, receiver, reasonHtml),
                "ticket_transfer_cancelled", MessageCategory.System);
        }
    }

    /// <summary>The invited person may have no account yet, so it goes to the address the holder typed.</summary>
    public EmailMessage ResaleInvitation(
        string toEmail, string holderName, string eventName, string claimUrl, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            return new EmailMessage(
                toEmail, null, null,
                Lf("Tickets_Resale_Email_Invite_Subject", eventName),
                Lf("Tickets_Resale_Email_Invite_Body", Encode(holderName), Encode(eventName), Encode(claimUrl)),
                "ticket_resale_invitation", MessageCategory.Ticketing);
        }
    }

    /// <summary>
    /// One per ticket holder with an email, to the holder, carrying the tickets that ride on theirs (children). System:
    /// a ticket is not optional mail.
    /// </summary>
    public EmailMessage TicketIssued(IssuedTicketMail ticket, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var eventName = Encode(ticket.EventName);
            var boughtBy = ticket.BuyerName is { } buyer
                ? $"<p>{Lf("Tickets_Delivery_Ticket_BoughtBy", Encode(buyer))}</p>"
                : string.Empty;
            var cards = string.Concat(ticket.Tickets.Select(card => $"""
                <div style="border: 2px dashed #c9a96e; border-radius: 12px; padding: 20px; margin: 20px 0; text-align: center; background: #ffffff;">
                    <div style="font-size: 12px; letter-spacing: 0.15em; text-transform: uppercase; color: #8b7355;">{eventName}</div>
                    <div style="font-family: Georgia, serif; font-size: 24px; color: #3d2b1f; margin: 6px 0 2px 0;">{Encode(card.HolderName)}</div>
                    <div style="font-size: 14px; color: #6b5a4e;">{Encode(localizer[$"Tickets_Delivery_Kind_{card.Kind}"].Value)}</div>
                    <img src="{Encode(card.CodeImageUrl.ToString())}" width="240" height="240" alt="{Encode(card.Barcode)}" style="display: block; margin: 16px auto; width: 240px; height: 240px;">
                    <div style="font-family: monospace; font-size: 16px; letter-spacing: 0.1em;">{Encode(card.Barcode)}</div>
                </div>
                """));
            var riders = ticket.Tickets.Count > 1
                ? $"<p>{localizer["Tickets_Delivery_Ticket_Riders"].Value}</p>"
                : string.Empty;
            var body = $"""
                <h2>{Lf("Tickets_Delivery_Ticket_Heading", eventName)}</h2>
                <p>{Lf("Tickets_Delivery_Ticket_Intro", Encode(ticket.HolderName))}</p>
                {boughtBy}
                {riders}
                {cards}
                <p>{localizer["Tickets_Delivery_Ticket_AtTheGate"].Value}</p>
                <p><a href="{Encode(ticket.MyTicketsUrl.ToString())}">{localizer["Tickets_Delivery_Ticket_Open"].Value}</a></p>
                <h3>{localizer["Tickets_Delivery_Rules_Heading"].Value}</h3>
                <ul>
                    <li>{localizer["Tickets_Delivery_Rules_Named"].Value}</li>
                    <li>{localizer["Tickets_Delivery_Rules_NoTransfer"].Value}</li>
                    <li>{localizer["Tickets_Delivery_Rules_Return"].Value}</li>
                    <li>{localizer["Tickets_Delivery_Rules_Private"].Value}</li>
                </ul>
                """;
            return new EmailMessage(
                ticket.HolderEmail, ticket.HolderName, ticket.HolderUserId,
                Lf("Tickets_Delivery_Ticket_Subject", ticket.EventName),
                body, "ticket_issued", MessageCategory.System, Culture: culture, DeliveryId: ticket.DeliveryId, Urgent: true);
        }
    }

    /// <summary>A plain receipt to the payer, never an invoice: the ticket part with its VAT, each donation without.</summary>
    public EmailMessage OrderReceipt(OrderReceiptMail receipt, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var rows = string.Concat(receipt.Lines.Select(line =>
            {
                var label = line.Kind == CheckoutLineKind.Ticket
                    ? Lf("Tickets_Delivery_Receipt_TicketLine", Encode(line.AttendeeName ?? string.Empty), (line.VatRate * 100m).ToString("0.##", CultureInfo.CurrentCulture))
                    : Lf("Tickets_Delivery_Receipt_DonationLine", Encode(localizer[$"Tickets_Buy_Purpose_{line.DonationPurpose ?? TicketDonationPurpose.General}"].Value));
                return $"<tr><td>{label}</td><td style=\"text-align: right;\">{Money(line.AmountMinor)}</td></tr>";
            }));
            var refunded = receipt.RefundedMinor > 0
                ? $"<tr><td>{localizer["Tickets_Delivery_Receipt_Refunded"].Value}</td><td style=\"text-align: right;\">-{Money(receipt.RefundedMinor)}</td></tr>"
                : string.Empty;
            var method = string.IsNullOrWhiteSpace(receipt.PaymentMethod)
                ? string.Empty
                : $"<br>{Lf("Tickets_Delivery_Receipt_Method", Encode(receipt.PaymentMethod))}";
            var donationNote = receipt.Lines.Any(l => l.Kind == CheckoutLineKind.Donation)
                ? $"<p>{localizer["Tickets_Delivery_Receipt_DonationNote"].Value}</p>"
                : string.Empty;
            var body = $"""
                <h2>{Lf("Tickets_Delivery_Receipt_Heading", Encode(receipt.EventName))}</h2>
                <p>{Lf("Tickets_Delivery_Receipt_Intro", Encode(receipt.BuyerName))}</p>
                <p>{Lf("Tickets_Delivery_Receipt_Reference", Encode(receipt.Reference))}<br>
                {Lf("Tickets_Delivery_Receipt_PaidOn", Encode(receipt.PaidOn.ToString("d", CultureInfo.CurrentCulture)))}{method}</p>
                <table style="width: 100%; border-collapse: collapse;">
                    <tbody>{rows}</tbody>
                    <tfoot>
                        <tr><th style="text-align: left;">{localizer["Tickets_Buy_Total"].Value}</th><th style="text-align: right;">{Money(receipt.TotalMinor)}</th></tr>
                        {refunded}
                    </tfoot>
                </table>
                {donationNote}
                <p>{localizer["Tickets_Delivery_Receipt_NotInvoice"].Value}</p>
                <p><a href="{Encode(receipt.OrderUrl.ToString())}">{localizer["Tickets_Delivery_Receipt_Open"].Value}</a></p>
                """;
            return new EmailMessage(
                receipt.BuyerEmail, receipt.BuyerName, receipt.BuyerUserId,
                Lf("Tickets_Delivery_Receipt_Subject", receipt.Reference),
                body, "ticket_order_receipt", MessageCategory.System, Culture: culture, DeliveryId: receipt.DeliveryId, Urgent: true);
        }
    }

    /// <summary>Sent a while after the ticket: a recruiting nudge, so it sits in the never-locked VolunteerUpdates opt-out.</summary>
    public EmailMessage JoinInvitation(
        Guid holderUserId, string holderEmail, string holderName, string eventName, Uri profileUrl, Uri shiftsUrl,
        Guid? deliveryId, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var body = $"""
                <h2>{Lf("Tickets_Delivery_Join_Heading", Encode(eventName))}</h2>
                <p>{Lf("Tickets_Delivery_Join_Intro", Encode(holderName), Encode(eventName))}</p>
                <p>{localizer["Tickets_Delivery_Join_Why"].Value}</p>
                <ol>
                    <li>{Lf("Tickets_Delivery_Join_StepProfile", Encode(profileUrl.ToString()))}</li>
                    <li>{Lf("Tickets_Delivery_Join_StepShifts", Encode(shiftsUrl.ToString()))}</li>
                </ol>
                <p>{localizer["Tickets_Delivery_Join_Closing"].Value}</p>
                """;
            return new EmailMessage(
                holderEmail, holderName, holderUserId,
                Lf("Tickets_Delivery_Join_Subject", eventName),
                body, "ticket_join_invitation", MessageCategory.VolunteerUpdates, Culture: culture, DeliveryId: deliveryId);
        }
    }

    private static string Money(long minor) =>
        WebUtility.HtmlEncode((minor / 100m).ToString("N2", CultureInfo.CurrentCulture) + " €");

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}

/// <summary><paramref name="Tickets"/> starts with the holder's own ticket.</summary>
internal sealed record IssuedTicketMail(
    Guid? HolderUserId,
    string HolderEmail,
    string HolderName,
    string EventName,
    IReadOnlyList<TicketCard> Tickets,
    Uri MyTicketsUrl,
    string? BuyerName,
    Guid? DeliveryId);

/// <summary><paramref name="Kind"/> names a <c>Tickets_Delivery_Kind_*</c> key.</summary>
internal sealed record TicketCard(string HolderName, string Kind, string Barcode, Uri CodeImageUrl);

internal sealed record OrderReceiptMail(
    Guid BuyerUserId,
    string BuyerEmail,
    string BuyerName,
    string EventName,
    string Reference,
    NodaTime.LocalDate PaidOn,
    string? PaymentMethod,
    IReadOnlyList<TicketCheckoutOrderLine> Lines,
    long TotalMinor,
    long RefundedMinor,
    Uri OrderUrl,
    Guid? DeliveryId);
