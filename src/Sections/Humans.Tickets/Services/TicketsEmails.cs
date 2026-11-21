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

/// <summary>Member-facing copy renders in the recipient's culture.</summary>
internal sealed class TicketsEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<TicketsResource> localizer,
    ILogger<TicketsEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>Always English, to the ticketing team's mailbox: what the daily reconciliation found wrong.</summary>
    public EmailMessage ReconciliationAlert(IReadOnlyList<string> findings, string reconUrl)
    {
        var fullUrl = $"{_settings.BaseUrl.TrimEnd('/')}{(reconUrl.StartsWith('/') ? "" : "/")}{reconUrl}";
        var items = string.Concat(findings.Select(f => $"<li>{Encode(f)}</li>"));
        return new EmailMessage(
            TicketConstants.TicketsTeamEmail, "Ticket team", UserId: null,
            "Ticket reconciliation needs a look",
            $"""
                <p>Last night's ticket reconciliation found money that does not add up:</p>
                <ul>{items}</ul>
                <p>Research each difference on the reconciliation page:
                <a href="{Encode(fullUrl)}">Open reconciliation</a></p>
                <p>This mail repeats every night until the reconciliation balances.</p>
                """,
            "ticket_reconciliation_alert", MessageCategory.System);
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

    /// <summary>
    /// The checkout ended without payment: <paramref name="failed"/> when the payment was declined, otherwise the hold
    /// lapsed. A payment that still lands later issues the tickets anyway, so the mail never says nothing was charged.
    /// </summary>
    public EmailMessage PaymentEnded(
        Guid buyerUserId, string buyerEmail, string buyerName, string eventName, string reference, bool failed, Uri buyUrl,
        Guid? deliveryId, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var kind = failed ? "Failed" : "Lapsed";
            var body = $"""
                <p>{Lf($"Tickets_Email_Payment{kind}_Intro", Encode(buyerName), Encode(reference), Encode(eventName))}</p>
                <p>{localizer["Tickets_Email_PaymentEnded_Late"].Value}</p>
                <p><a href="{Encode(buyUrl.ToString())}">{localizer["Tickets_Email_PaymentEnded_Retry"].Value}</a></p>
                """;
            return new EmailMessage(
                buyerEmail, buyerName, buyerUserId,
                Lf($"Tickets_Email_Payment{kind}_Subject", eventName),
                body, "ticket_payment_ended", MessageCategory.System, Culture: culture, DeliveryId: deliveryId);
        }
    }

    /// <summary>The holder's returned ticket sold; the refund follows in the evening batch.</summary>
    public EmailMessage ResaleSold(
        Guid holderUserId, string holderEmail, string holderName, string eventName, long refundMinor, Uri ticketsUrl,
        Guid? deliveryId, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var body = $"""
                <p>{Lf("Tickets_Email_ResaleSold_Intro", Encode(holderName), Encode(eventName))}</p>
                <p>{Lf("Tickets_Email_ResaleSold_Refund", Money(refundMinor))}</p>
                <p><a href="{Encode(ticketsUrl.ToString())}">{localizer["Tickets_Email_Resale_Open"].Value}</a></p>
                """;
            return new EmailMessage(
                holderEmail, holderName, holderUserId,
                Lf("Tickets_Email_ResaleSold_Subject", eventName),
                body, "ticket_resale_sold", MessageCategory.System, Culture: culture, DeliveryId: deliveryId);
        }
    }

    /// <summary>Sales closed with the holder's ticket still in the queue: no refund, and the ticket still admits them.</summary>
    public EmailMessage ResaleUnsold(
        Guid holderUserId, string holderEmail, string holderName, string eventName, Uri ticketsUrl,
        Guid? deliveryId, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var body = $"""
                <p>{Lf("Tickets_Email_ResaleUnsold_Intro", Encode(holderName), Encode(eventName))}</p>
                <p>{localizer["Tickets_Email_ResaleUnsold_NoRefund"].Value}</p>
                <p><a href="{Encode(ticketsUrl.ToString())}">{localizer["Tickets_Email_Resale_Open"].Value}</a></p>
                """;
            return new EmailMessage(
                holderEmail, holderName, holderUserId,
                Lf("Tickets_Email_ResaleUnsold_Subject", eventName),
                body, "ticket_resale_unsold", MessageCategory.System, Culture: culture, DeliveryId: deliveryId);
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
