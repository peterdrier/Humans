using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Tickets.Services.Delivery;

/// <summary>
/// Everything a buyer and each ticket holder receive once an order's tickets exist: one ticket email per named
/// person, one receipt to the payer, and a later invitation to volunteer. Reads the checkout repository directly
/// because the checkout service calls in here, and a service-to-service loop would not resolve.
/// </summary>
internal sealed class TicketDeliveryService(
    ITicketCheckoutRepository repository,
    ISettingsService settings,
    IUserService users,
    IEmailService email,
    TicketsEmails emails,
    TicketCodeImages codeImages,
    IBackgroundJobClient jobs,
    IAuditLogService auditLog,
    IOptions<EmailSettings> emailSettings,
    ILogger<TicketDeliveryService> logger) : ITicketDelivery
{
    private const string JobName = "TicketDelivery";

    /// <summary>Long enough that it does not land beside the ticket, soon enough that the purchase is fresh.</summary>
    internal static readonly TimeSpan JoinInvitationDelay = TimeSpan.FromDays(2);

    public async Task TicketsIssuedAsync(Guid orderId, CancellationToken ct = default)
    {
        var sent = await SendAsync(orderId, fresh: false, ct);
        if (sent is null)
            return;

        foreach (var ticket in sent.Value.Tickets)
            jobs.Schedule<ITicketDelivery>(d => d.SendJoinInvitationAsync(ticket.Id, CancellationToken.None), JoinInvitationDelay);

        await auditLog.LogAsync(TicketsAuditActions.TicketDeliverySent, TicketCheckoutService.AuditEntityType, orderId,
            $"Queued {sent.Value.Tickets.Count} ticket email(s) and the receipt", JobName);
    }

    public async Task<CheckoutMutationResult> ResendAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var sent = await SendAsync(orderId, fresh: true, ct);
        if (sent is null)
            return new("Only a paid order with issued tickets can be resent.");

        await auditLog.LogAsync(TicketsAuditActions.TicketDeliveryResent, TicketCheckoutService.AuditEntityType, orderId,
            $"Resent {sent.Value.Tickets.Count} ticket email(s) and the receipt", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task SendJoinInvitationAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.State != TicketCheckoutTicketState.Valid || ticket.HolderUserId is not { } holderId)
            return;
        var eventName = await EventNameAsync(ticket.SalesEventId, ct);
        if (eventName is null)
            return;

        var holder = await users.GetUserInfoAsync(holderId, ct);
        var baseUrl = BaseUrl;
        await email.SendAsync(emails.JoinInvitation(
            holderId, holder?.Email ?? ticket.HolderEmail, ticket.HolderName, eventName,
            new Uri($"{baseUrl}/Profile"), new Uri($"{baseUrl}/Shifts"),
            DeliveryIdFor("ticket_join_invitation", ticket.Id), CultureOf(holder)), ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketJoinInvitationSent, TicketCheckoutService.AuditEntityType, ticket.OrderId,
            "Queued the volunteer invitation to a ticket holder", JobName, ticket.Id, "TicketCheckoutTicket");
    }

    /// <summary>
    /// Null when there is nothing to send. <paramref name="fresh"/> sends again even when an earlier send of the same
    /// mail is still in the outbox; otherwise a repeated call is a no-op per mail.
    /// </summary>
    private async Task<(TicketCheckoutOrder Order, IReadOnlyList<TicketCheckoutTicket> Tickets)?> SendAsync(
        Guid orderId, bool fresh, CancellationToken ct)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null || order.State != TicketCheckoutOrderState.Paid)
            return null;
        var salesEvent = await repository.GetSalesEventAsync(order.SalesEventId, ct);
        var eventSettings = salesEvent is null ? null : await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct);
        if (salesEvent is null || eventSettings is null)
        {
            logger.LogError("Order {OrderId} is paid but its event is gone; no ticket mail sent", orderId);
            return null;
        }

        var tickets = (await repository.GetTicketsForOrderAsync(orderId, ct))
            .Where(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered)
            .ToList();
        var baseUrl = BaseUrl;
        var buyer = await users.GetUserInfoAsync(order.BuyerUserId, ct);

        foreach (var ticket in tickets)
        {
            var holder = ticket.HolderUserId is { } holderId ? await users.GetUserInfoAsync(holderId, ct) : null;
            var boughtBySomeoneElse = ticket.HolderUserId != order.BuyerUserId;
            await email.SendAsync(emails.TicketIssued(new IssuedTicketMail(
                ticket.HolderUserId,
                holder?.Email ?? ticket.HolderEmail,
                ticket.HolderName,
                eventSettings.EventName,
                ticket.Kind.ToString(),
                ticket.Barcode,
                codeImages.ImageUrl(ticket.Barcode),
                new Uri($"{baseUrl}/Tickets/Mine"),
                boughtBySomeoneElse ? order.BuyerName : null,
                fresh ? null : DeliveryIdFor("ticket_issued", ticket.Id)), CultureOf(holder ?? buyer)), ct);
        }

        var paidOn = (order.PaidAt ?? order.UpdatedAt).InZone(eventSettings.TimeZone).Date;
        await email.SendAsync(emails.OrderReceipt(new OrderReceiptMail(
            order.BuyerUserId,
            buyer?.Email ?? order.BuyerEmail,
            order.BuyerName,
            eventSettings.EventName,
            Reference(order.Id),
            paidOn,
            order.PaymentMethod,
            order.Lines,
            order.TotalMinor,
            order.RefundedMinor,
            new Uri($"{baseUrl}/Tickets/Buy/Order/{order.Id}"),
            fresh ? null : DeliveryIdFor("ticket_order_receipt", order.Id)), CultureOf(buyer)), ct);

        return (order, tickets);
    }

    /// <summary>Short enough to read out to the ticketing team.</summary>
    internal static string Reference(Guid orderId) => orderId.ToString("N")[..8].ToUpperInvariant();

    /// <summary>Stable per mail and subject, so a repeated hand-off reuses the outbox row instead of mailing twice.</summary>
    internal static Guid DeliveryIdFor(string template, Guid subjectId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{template}:{subjectId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private string BaseUrl => emailSettings.Value.BaseUrl.TrimEnd('/');

    private async Task<string?> EventNameAsync(Guid salesEventId, CancellationToken ct)
    {
        var salesEvent = await repository.GetSalesEventAsync(salesEventId, ct);
        return salesEvent is null ? null : (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName;
    }

    private static string CultureOf(UserInfo? user) =>
        user?.PreferredLanguage is { } language && language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode;
}
