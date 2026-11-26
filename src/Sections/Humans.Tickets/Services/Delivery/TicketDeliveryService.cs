using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Services.Delivery;

/// <summary>
/// The checkout's hand-off: it only enqueues, so a failure while sending is retried by Hangfire instead of lost
/// inside the paid transition. The stable delivery ids keep a retry from mailing anyone twice.
/// </summary>
internal sealed class TicketDeliveryHandOff(IBackgroundJobClient jobs) : ITicketDelivery
{
    public Task TicketsIssuedAsync(Guid orderId, CancellationToken ct = default)
    {
        jobs.Enqueue<TicketDeliveryService>(d => d.DeliverAsync(orderId, CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task PaymentEndedAsync(Guid orderId, CancellationToken ct = default)
    {
        jobs.Enqueue<TicketDeliveryService>(d => d.SendPaymentEndedAsync(orderId, CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task ResaleSoldAsync(Guid ticketId, long refundMinor, CancellationToken ct = default)
    {
        jobs.Enqueue<TicketDeliveryService>(d => d.SendResaleSoldAsync(ticketId, refundMinor, CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task ResaleUnsoldAsync(Guid ticketId, CancellationToken ct = default)
    {
        jobs.Enqueue<TicketDeliveryService>(d => d.SendResaleUnsoldAsync(ticketId, CancellationToken.None));
        return Task.CompletedTask;
    }

    /// <summary>Each rename is its own mail; the id minted here keeps a Hangfire retry of it from mailing twice.</summary>
    public Task TicketRenamedAsync(Guid ticketId, CancellationToken ct = default)
    {
        var deliveryId = Guid.NewGuid();
        jobs.Enqueue<TicketDeliveryService>(d => d.SendRenamedTicketAsync(ticketId, deliveryId, CancellationToken.None));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Everything a buyer and each ticket holder receive once an order's tickets exist: one ticket email per named
/// person, one receipt to the buyer, and a later invitation to volunteer.
/// </summary>
internal sealed class TicketDeliveryService(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    IUserService users,
    IEmailService email,
    TicketsEmails emails,
    TicketCodeImages codeImages,
    IBackgroundJobClient jobs,
    IAuditLogService auditLog,
    IOptions<EmailSettings> emailSettings,
    ILogger<TicketDeliveryService> logger)
{
    private const string JobName = "TicketDelivery";

    /// <summary>Long enough that it does not land beside the ticket, soon enough that the order is fresh.</summary>
    internal static readonly TimeSpan JoinInvitationDelay = TimeSpan.FromDays(2);

    /// <summary>The Hangfire job behind <see cref="TicketDeliveryHandOff"/>. Throws on failure so Hangfire retries.</summary>
    public async Task DeliverAsync(Guid orderId, CancellationToken ct = default)
    {
        var sent = await SendAsync(orderId, fresh: false, ct);
        if (sent is null)
            return;

        foreach (var ticket in sent.Tickets.Where(t => t.HolderUserId is not null && t.Kind != TicketHolderKind.Child))
            jobs.Schedule<TicketDeliveryService>(
                d => d.SendJoinInvitationAsync(orderId, ticket.Id, CancellationToken.None), JoinInvitationDelay);

        await auditLog.LogAsync(TicketsAuditActions.TicketDeliverySent, TicketCheckoutService.AuditEntityType, orderId,
            sent.Describe(), JobName);
        await RecordOnTimelineAsync(orderId, TicketCheckoutOrderEventKind.TicketsEmailed, sent, null, ct);
    }

    /// <summary>The ticketing team's resend: every ticket email and the receipt go out again, no second invitation.</summary>
    public async Task<CheckoutMutationResult> ResendAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var detail = await checkout.GetOrderDetailAsync(orderId, ct);
        if (detail?.Tickets.Any(t => t.IsHeld) != true)
            return new("Only a paid order with a live ticket can be resent.");
        var sent = await SendAsync(orderId, fresh: true, ct);
        if (sent is null)
            return new("Only a paid order with a live ticket can be resent.");

        await auditLog.LogAsync(TicketsAuditActions.TicketDeliveryResent, TicketCheckoutService.AuditEntityType, orderId,
            sent.Describe(), actorUserId);
        await RecordOnTimelineAsync(orderId, TicketCheckoutOrderEventKind.TicketsResent, sent, actorUserId, ct);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>The scheduled follow-up; skipped once the ticket is no longer valid.</summary>
    public async Task SendJoinInvitationAsync(Guid orderId, Guid ticketId, CancellationToken ct = default)
    {
        var detail = await checkout.GetOrderDetailAsync(orderId, ct);
        var ticket = detail?.Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (detail is null || ticket is null || ticket.State != TicketCheckoutTicketState.Valid
            || ticket.HolderUserId is not { } holderId)
            return;
        var eventName = await EventNameAsync(ticket.TicketingSettingsId, ct);
        if (eventName is null)
            return;

        var holder = await users.GetUserInfoAsync(holderId, ct);
        if (holder is null || holder.IsTombstone || (holder.Email ?? ticket.HolderEmail) is not { } address)
            return;
        var baseUrl = BaseUrl;
        await email.SendAsync(emails.JoinInvitation(
            holder.Id, address, ticket.HolderName, eventName,
            new Uri($"{baseUrl}/Profile"), new Uri($"{baseUrl}/Shifts"),
            DeliveryIdFor("ticket_join_invitation", ticket.Id), CultureOf(holder, detail.Order)), ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketJoinInvitationSent, TicketCheckoutService.AuditEntityType, orderId,
            "Handed the volunteer invitation for a ticket holder to Email (it honours their opt-out)",
            JobName, ticket.Id, "TicketCheckoutTicket");
    }

    /// <summary>The buyer of an order that ended unpaid; skipped if it was paid after all.</summary>
    public async Task SendPaymentEndedAsync(Guid orderId, CancellationToken ct = default)
    {
        var detail = await checkout.GetOrderDetailAsync(orderId, ct);
        if (detail?.Order is not { State: TicketCheckoutOrderState.Failed or TicketCheckoutOrderState.Expired } order
            || await EventNameAsync(order.TicketingSettingsId, ct) is not { } eventName)
            return;
        var buyer = await users.GetUserInfoAsync(order.BuyerUserId, ct);
        var failed = order.State == TicketCheckoutOrderState.Failed;
        await email.SendAsync(emails.PaymentEnded(
            order.BuyerUserId, buyer?.Email ?? order.BuyerEmail, order.BuyerName, eventName, order.Reference, failed,
            new Uri($"{BaseUrl}/Tickets/Buy"), DeliveryIdFor("ticket_payment_ended", order.Id), CultureOf(buyer, order)), ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketHolderNotified, TicketCheckoutService.AuditEntityType, orderId,
            failed ? "Told the buyer the payment failed" : "Told the buyer the order lapsed unpaid", JobName);
    }

    /// <summary>The holder of a resold ticket, with the refund the evening batch sends.</summary>
    public async Task SendResaleSoldAsync(Guid ticketId, long refundMinor, CancellationToken ct = default)
    {
        if (await HolderMailAsync(ticketId, TicketCheckoutTicketState.Resold, ct) is not { } to)
            return;
        await email.SendAsync(emails.ResaleSold(
            to.UserId, to.Email, to.Ticket.HolderName, to.EventName, refundMinor, new Uri($"{BaseUrl}/Tickets/Buy/Mine"),
            DeliveryIdFor("ticket_resale_sold", ticketId), to.Culture), ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketHolderNotified, TicketCheckoutService.AuditEntityType, to.Ticket.OrderId,
            $"Told the holder ticket {to.Ticket.Barcode} was resold and its refund follows in the evening batch", JobName,
            to.UserId, AuditSubjectTypes.User);
    }

    /// <summary>The holder whose ticket was still in the resale queue at sales close.</summary>
    public async Task SendResaleUnsoldAsync(Guid ticketId, CancellationToken ct = default)
    {
        if (await HolderMailAsync(ticketId, TicketCheckoutTicketState.Offered, ct) is not { } to)
            return;
        await email.SendAsync(emails.ResaleUnsold(
            to.UserId, to.Email, to.Ticket.HolderName, to.EventName, new Uri($"{BaseUrl}/Tickets/Buy/Mine"),
            DeliveryIdFor("ticket_resale_unsold", ticketId), to.Culture), ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketHolderNotified, TicketCheckoutService.AuditEntityType, to.Ticket.OrderId,
            $"Told the holder ticket {to.Ticket.Barcode} did not sell before sales closed: no refund, the ticket stays valid", JobName,
            to.UserId, AuditSubjectTypes.User);
    }

    /// <summary>A renamed ticket goes to its holder again under the new name, with any children riding on it.</summary>
    public async Task SendRenamedTicketAsync(Guid ticketId, Guid deliveryId, CancellationToken ct = default)
    {
        if (await HolderMailAsync(ticketId, TicketCheckoutTicketState.Valid, ct) is not { } to)
            return;
        var riders = ((await checkout.GetOrderDetailAsync(to.Ticket.OrderId, ct))?.Tickets ?? [])
            .Where(t => t.ParentTicketId == ticketId && t.State == TicketCheckoutTicketState.Valid && string.IsNullOrWhiteSpace(t.HolderEmail));
        await email.SendAsync(emails.TicketIssued(new IssuedTicketMail(
            to.UserId, to.Email, to.Ticket.HolderName, to.EventName, [Card(to.Ticket), .. riders.Select(Card)],
            new Uri($"{BaseUrl}/Tickets/Buy/Mine"), null, deliveryId), to.Culture), ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketHolderNotified, TicketCheckoutService.AuditEntityType, to.Ticket.OrderId,
            $"Sent renamed ticket {to.Ticket.Barcode} to its holder", JobName, to.UserId, AuditSubjectTypes.User);
    }

    private sealed record HolderMail(TicketCheckoutTicket Ticket, Guid UserId, string Email, string EventName, string Culture);

    /// <summary>Null, and nothing sent, once the ticket has moved on from <paramref name="state"/> or has no holder.</summary>
    private async Task<HolderMail?> HolderMailAsync(Guid ticketId, TicketCheckoutTicketState state, CancellationToken ct)
    {
        if (await checkout.GetTicketAsync(ticketId, ct) is not { HolderUserId: { } holderId } ticket || ticket.State != state
            || await EventNameAsync(ticket.TicketingSettingsId, ct) is not { } eventName)
            return null;
        var holder = await users.GetUserInfoAsync(holderId, ct);
        if ((holder?.Email ?? ticket.HolderEmail) is not { } address)
            return null;
        var order = (await checkout.GetOrderDetailAsync(ticket.OrderId, ct))?.Order;
        return new HolderMail(ticket, holderId, address, eventName,
            order is null ? SupportedOr(holder?.PreferredLanguage) : CultureOf(holder, order));
    }

    /// <summary>
    /// Null when there is nothing to send. <paramref name="fresh"/> sends again even when an earlier send of the same
    /// mail is still in the outbox; otherwise a repeated call is a no-op per mail.
    /// </summary>
    private async Task<DeliveryOutcome?> SendAsync(Guid orderId, bool fresh, CancellationToken ct)
    {
        var detail = await checkout.GetOrderDetailAsync(orderId, ct);
        if (detail is null || detail.Order.State != TicketCheckoutOrderState.Paid)
            return null;
        var order = detail.Order;
        var ticketing = await checkout.GetTicketingSettingsAsync(order.TicketingSettingsId, ct);
        var eventSettings = ticketing is null ? null : await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct);
        if (ticketing is null || eventSettings is null)
        {
            logger.LogError("Order {OrderId} is paid but its event is gone; no ticket mail sent", orderId);
            return null;
        }

        var tickets = detail.Tickets
            .Where(t => t.IsHeld)
            .ToList();
        var baseUrl = BaseUrl;
        var buyer = await users.GetUserInfoAsync(order.BuyerUserId, ct);

        // Each ticket with an email gets its own mail; one without (a child) rides on its adult's.
        var mailed = tickets.Where(t => !string.IsNullOrWhiteSpace(t.HolderEmail) || t.ParentTicketId is null).ToList();
        int ticketMails = 0, skipped = 0;
        foreach (var ticket in mailed)
        {
            var holder = ticket.HolderUserId is { } holderId ? await users.GetUserInfoAsync(holderId, ct) : null;
            if ((holder?.Email ?? ticket.HolderEmail ?? (ticket.HolderUserId == order.BuyerUserId ? buyer?.Email ?? order.BuyerEmail : null)) is not { } address)
            {
                logger.LogWarning("Ticket {TicketId} on order {OrderId} has no address; its ticket email was not sent", ticket.Id, orderId);
                skipped++;
                continue;
            }
            var riders = tickets.Where(t => t.ParentTicketId == ticket.Id && !mailed.Contains(t));
            await email.SendAsync(emails.TicketIssued(new IssuedTicketMail(
                ticket.HolderUserId,
                address,
                ticket.HolderName,
                eventSettings.EventName,
                [Card(ticket), .. riders.Select(Card)],
                new Uri($"{baseUrl}/Tickets/Buy/Mine"),
                ticket.HolderUserId != order.BuyerUserId ? order.BuyerName : null,
                fresh ? null : DeliveryIdFor("ticket_issued", ticket.Id)), CultureOf(holder, order)), ct);
            ticketMails++;
        }

        var paidOn = (order.PaidAt ?? order.UpdatedAt).InZone(eventSettings.TimeZone).Date;
        await email.SendAsync(emails.OrderReceipt(new OrderReceiptMail(
            order.BuyerUserId,
            buyer?.Email ?? order.BuyerEmail,
            order.BuyerName,
            eventSettings.EventName,
            order.Reference,
            paidOn,
            order.PaymentMethod,
            order.Lines,
            order.TotalMinor,
            order.RefundedMinor,
            new Uri($"{baseUrl}/Tickets/Buy/Order/{order.Id}"),
            fresh ? null : DeliveryIdFor("ticket_order_receipt", order.Id)), SupportedOr(order.Culture)), ct);

        return new DeliveryOutcome(tickets, ticketMails, skipped);
    }

    /// <summary>The order timeline is where the ticketing team looks when a ticket "never arrived".</summary>
    private async Task RecordOnTimelineAsync(
        Guid orderId, TicketCheckoutOrderEventKind kind, DeliveryOutcome sent, Guid? actorUserId, CancellationToken ct)
    {
        await checkout.RecordDeliveryAsync(orderId, kind, sent.Describe(), actorUserId, ct);
        if (sent.Skipped > 0)
            await checkout.RecordDeliveryAsync(orderId, TicketCheckoutOrderEventKind.TicketMailSkipped,
                $"{sent.Skipped} ticket(s) have no email address, so their ticket email was not sent", actorUserId, ct);
    }

    /// <summary>Stable per mail and subject, so a repeated hand-off reuses the outbox row instead of mailing twice.</summary>
    internal static Guid DeliveryIdFor(string template, Guid subjectId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{template}:{subjectId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private TicketCard Card(TicketCheckoutTicket ticket) =>
        new(ticket.HolderName, ticket.Kind, ticket.Barcode, codeImages.ImageUrl(ticket.Barcode)) { IssuedAt = ticket.IssuedAt };

    private string BaseUrl => emailSettings.Value.BaseUrl.TrimEnd('/');

    private async Task<string?> EventNameAsync(Guid settingsId, CancellationToken ct)
    {
        var ticketing = await checkout.GetTicketingSettingsAsync(settingsId, ct);
        return ticketing is null ? null : (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName;
    }

    /// <summary>
    /// An account that has never signed in carries the default language, not a choice; until its
    /// holder has signed in, the language the order was placed in is the better guess.
    /// </summary>
    private static string CultureOf(UserInfo? holder, TicketCheckoutOrder order) =>
        SupportedOr(holder is { LastSignInAt: not null } ? holder.PreferredLanguage : order.Culture);

    private static string SupportedOr(string? language) =>
        language is not null && language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode;

    private sealed record DeliveryOutcome(IReadOnlyList<TicketCheckoutTicket> Tickets, int TicketMails, int Skipped)
    {
        public string Describe() =>
            $"Handed {TicketMails} ticket email(s) for {Tickets.Count} live ticket(s) and the receipt to Email"
            + (Skipped > 0 ? $"; {Skipped} skipped for lack of an address" : "");
    }
}
