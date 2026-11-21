using System.Globalization;
using System.Security.Cryptography;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed class TicketCheckoutService(
    ITicketCheckoutRepository repository,
    IEnumerable<ITicketCheckoutBackend> backends,
    IGrantService grants,
    ISettingsService settings,
    IUserService users,
    IAccountProvisioningService provisioning,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<TicketCheckoutService> logger) : ITicketCheckoutService, IUserDataContributor, IUserMerge
{
    internal const string AuditEntityType = "TicketCheckoutOrder";
    internal const string SalesEventAuditEntityType = "TicketSalesEvent";
    private const string JobName = "TicketCheckout";

    /// <summary>Long enough to pay with a bank redirect; Stripe accepts 30 minutes to 24 hours.</summary>
    internal static readonly Duration CheckoutLifetime = Duration.FromHours(1);

    private readonly IReadOnlyList<ITicketCheckoutBackend> _backends = backends.ToList();

    public IReadOnlyList<CheckoutBackendInfo> Backends =>
        _backends.Select(b => new CheckoutBackendInfo(b.Key, b.IsConfigured)).OrderBy(b => b.Key, StringComparer.Ordinal).ToList();

    private ITicketCheckoutBackend? BackendFor(string key) =>
        _backends.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));

    public Task<IReadOnlyList<TicketSalesEvent>> GetSalesEventsAsync(CancellationToken ct = default) =>
        repository.GetSalesEventsAsync(ct);

    public Task<TicketSalesEvent?> GetSalesEventAsync(Guid id, CancellationToken ct = default) =>
        repository.GetSalesEventAsync(id, ct);

    public async Task<CheckoutMutationResult> SaveSalesEventAsync(
        SalesEventSave save, Guid actorUserId, CancellationToken ct = default)
    {
        if (await settings.GetEventSettingsByIdAsync(save.EventSettingsId, ct) is null)
            return new("Pick an existing event.");
        if (BackendFor(save.BackendKey) is null)
            return new($"No checkout backend is registered under '{save.BackendKey}'.");
        if (save.Capacity < 0)
            return new("Capacity cannot be negative.");
        if (save.TicketVatRate is < 0 or >= 1)
            return new("VAT is a fraction between 0 and 1, e.g. 0.10.");
        if (save.Waves.Any(w => w.PriceMinor < 0) || save.Waves.GroupBy(w => w.EndsOn).Any(g => g.Count() > 1))
            return new("Waves need distinct end dates and non-negative prices.");
        if (save.PriceRules.Any(r => r.AmountMinor < 0))
            return new("Price rule amounts cannot be negative.");
        if (save.SupporterTotalsMinor.Any(t => t <= 0))
            return new("Supporter totals must be positive.");

        var existing = save.Id is { } id ? await repository.GetSalesEventAsync(id, ct) : null;
        if (save.Id is not null && existing is null)
            return new("That sales event no longer exists.");
        var clash = await repository.GetSalesEventForEventAsync(save.EventSettingsId, ct);
        if (clash is not null && clash.Id != existing?.Id)
            return new("That event already has a sales setup.");

        var now = clock.GetCurrentInstant();
        var salesEvent = new TicketSalesEvent
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            CreatedAt = existing?.CreatedAt ?? now,
            EventSettingsId = save.EventSettingsId,
            BackendKey = save.BackendKey,
            BackendEventRef = string.IsNullOrWhiteSpace(save.BackendEventRef) ? null : save.BackendEventRef.Trim(),
            Mode = save.Mode,
            SalesCloseAt = save.SalesCloseAt,
            Capacity = save.Capacity,
            TicketVatRate = save.TicketVatRate,
            Waves = save.Waves.OrderBy(w => w.EndsOn).ToList(),
            SupporterTotalsMinor = save.SupporterTotalsMinor.Distinct().Order().ToList(),
            PriceRules = save.PriceRules.ToList(),
            UpdatedAt = now,
        };
        await repository.SaveSalesEventAsync(salesEvent, ct);

        await auditLog.LogAsync(TicketsAuditActions.TicketSalesEventSaved, SalesEventAuditEntityType, salesEvent.Id,
            $"Sales setup saved: backend {salesEvent.BackendKey}, mode {salesEvent.Mode}, capacity {salesEvent.Capacity}, " +
            $"{salesEvent.Waves.Count} waves, {salesEvent.PriceRules.Count} price rules", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<BuyPage?> GetBuyPageAsync(Guid userId, CancellationToken ct = default)
    {
        var context = await LoadBuyContextAsync(userId, ct);
        if (context is null)
            return null;

        return new BuyPage(
            context.SalesEvent, context.Event.EventName, context.OwnPrice, context.PublicPrice,
            context.Unavailable, context.AlreadyHoldsTicket, context.Buyer?.FullName ?? string.Empty,
            context.UserOrders.Where(o => o.SalesEventId == context.SalesEvent.Id).ToList());
    }

    public async Task<StartCheckoutResult> StartCheckoutAsync(
        StartCheckout command, Guid userId, CancellationToken ct = default)
    {
        var context = await LoadBuyContextAsync(userId, ct);
        if (context is null)
            return StartCheckoutResult.Refused("Tickets_Buy_NotOnSale");
        if (context.Unavailable is { } unavailable)
            return StartCheckoutResult.Refused(unavailable);
        var buyer = context.Buyer!;

        var hasPartner = !string.IsNullOrWhiteSpace(command.PartnerName) || !string.IsNullOrWhiteSpace(command.PartnerEmail);
        if (hasPartner && (string.IsNullOrWhiteSpace(command.PartnerName) || !LooksLikeEmail(command.PartnerEmail)))
            return StartCheckoutResult.Refused("Tickets_Buy_PartnerIncomplete");
        if (command.ForSelf && context.AlreadyHoldsTicket)
            return StartCheckoutResult.Refused("Tickets_Buy_AlreadyHolds");
        if (command.DonationMinor < 0)
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");

        var salesEvent = context.SalesEvent;
        // Checked before the partner's account is created, so a refused order leaves nothing behind.
        var ticketCount = (command.ForSelf ? 1 : 0) + (hasPartner ? 1 : 0);
        if (ticketCount > 0 && salesEvent.Mode != TicketSalesMode.Open)
            return StartCheckoutResult.Refused("Tickets_Buy_ResaleOnly");
        var held = (await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct))
            .Where(o => o.HoldsTickets(context.Now))
            .Sum(o => o.Lines.Count(l => l.Kind == CheckoutLineKind.Ticket));
        if (held + ticketCount > salesEvent.Capacity)
            return StartCheckoutResult.Refused("Tickets_Buy_SoldOut");

        var lines = new List<TicketCheckoutOrderLine>();
        if (command.ForSelf)
            lines.Add(TicketLine(salesEvent, context.OwnPrice!, buyer.FullName, buyer.Email!, buyer.Id));

        if (hasPartner)
        {
            var partnerEmail = command.PartnerEmail!.Trim();
            if (string.Equals(partnerEmail, buyer.Email, StringComparison.OrdinalIgnoreCase))
                return StartCheckoutResult.Refused("Tickets_Buy_PartnerIsYou");
            // The ticket is the partner's: their account exists before they hold it, as in 2026.
            var partner = await provisioning.FindOrCreateUserByEmailAsync(
                partnerEmail, command.PartnerName!.Trim(), ContactSource.Manual, ct);
            if (context.UserOrders.Concat(await repository.GetOrdersForUserAsync(partner.UserId, ct))
                .Any(o => o.SalesEventId == salesEvent.Id && o.HoldsTickets(context.Now)
                          && o.Lines.Any(l => l.Kind == CheckoutLineKind.Ticket && l.AttendeeUserId == partner.UserId)))
                return StartCheckoutResult.Refused("Tickets_Buy_PartnerHolds");
            lines.Add(TicketLine(salesEvent, context.PublicPrice!, command.PartnerName!.Trim(), partnerEmail, partner.UserId));
        }

        if (command.SupporterTotalMinor is { } supporterTotal)
        {
            if (!command.ForSelf || !salesEvent.SupporterTotalsMinor.Contains(supporterTotal)
                || supporterTotal <= context.OwnPrice!.AmountMinor)
                return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
            lines.Add(DonationLine(supporterTotal - context.OwnPrice.AmountMinor, TicketDonationPurpose.General));
        }

        if (command.DonationMinor > 0)
            lines.Add(DonationLine(command.DonationMinor, command.DonationPurpose));

        if (lines.Count == 0)
            return StartCheckoutResult.Refused("Tickets_Buy_Empty");

        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEvent.Id,
            BuyerUserId = buyer.Id,
            BuyerName = buyer.FullName,
            BuyerEmail = buyer.Email!,
            BackendKey = salesEvent.BackendKey,
            State = TicketCheckoutOrderState.Created,
            TotalMinor = lines.Sum(l => l.AmountMinor),
            CreatedAt = context.Now,
            ExpiresAt = context.Now + CheckoutLifetime,
            UpdatedAt = context.Now,
            Lines = lines,
        };
        await repository.AddOrderAsync(order, ct);

        if (order.TotalMinor == 0)
        {
            // Nothing to charge (a free companion, credits covering the price): no backend has a zero checkout.
            MarkPaid(order, null, null, context.Now);
            await repository.UpdateOrderAsync(order, ct);
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutPaid, AuditEntityType, order.Id,
                $"Free order issued {ticketCount} ticket(s) without a payment", buyer.Id);
            return new StartCheckoutResult(order.Id, null, null);
        }

        var backend = BackendFor(order.BackendKey)!;
        try
        {
            var session = await backend.CreateCheckoutAsync(new CheckoutRequest(
                order.Id,
                salesEvent.EventSettingsId,
                salesEvent.BackendEventRef,
                order.BuyerName,
                order.BuyerEmail,
                order.Currency,
                command.Culture,
                order.Lines.Select(l => new CheckoutLine(l.Kind, l.Description, l.AmountMinor, l.VatRate, l.AttendeeName)).ToList(),
                order.ExpiresAt,
                new Uri(command.ReturnUrlBase, order.Id.ToString())), ct);

            order.BackendOrderId = session.BackendOrderId;
            order.PaymentUrl = session.PaymentUrl.ToString();
            order.State = TicketCheckoutOrderState.Pending;
            order.UpdatedAt = clock.GetCurrentInstant();
            await repository.UpdateOrderAsync(order, ct);
            return new StartCheckoutResult(order.Id, session.PaymentUrl, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checkout backend {Backend} refused order {OrderId}", order.BackendKey, order.Id);
            order.State = TicketCheckoutOrderState.Failed;
            order.UpdatedAt = clock.GetCurrentInstant();
            await repository.UpdateOrderAsync(order, ct);
            return StartCheckoutResult.Refused("Tickets_Buy_BackendFailed");
        }
    }

    public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default) =>
        repository.GetOrdersForUserAsync(userId, ct);

    public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
        repository.GetOrdersForSalesEventAsync(salesEventId, ct);

    public async Task<CheckoutOrderDetail?> GetOrderDetailAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        return order is null ? null : new CheckoutOrderDetail(order, await repository.GetRefundsAsync(orderId, ct));
    }

    public async Task CheckoutChangedAsync(string backendKey, string backendOrderId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderByBackendIdAsync(backendKey, backendOrderId, ct);
        if (order is null)
        {
            // A webhook for a session Humans never opened (another environment sharing the account): ignore it.
            logger.LogInformation("Checkout change for unknown {Backend} order {BackendOrderId}", backendKey, backendOrderId);
            return;
        }
        await RefreshAsync(order, ct);
    }

    public async Task<TicketCheckoutOrder?> RefreshOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null)
            return null;
        await RefreshAsync(order, ct);
        return order;
    }

    public async Task<int> ReconcileOpenOrdersAsync(CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var order in await repository.GetOpenOrdersAsync(ct))
        {
            try
            {
                if (await RefreshAsync(order, ct))
                    changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconcile of checkout order {OrderId} failed; next run retries", order.Id);
            }
        }
        return changed;
    }

    /// <summary>True when the state moved.</summary>
    private async Task<bool> RefreshAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (order.BackendOrderId is null)
        {
            // The backend never accepted it; once its hold lapses it is plainly expired.
            if (order.State != TicketCheckoutOrderState.Created || order.ExpiresAt > now)
                return false;
            order.State = TicketCheckoutOrderState.Expired;
            order.UpdatedAt = now;
            await repository.UpdateOrderAsync(order, ct);
            return true;
        }

        var backend = BackendFor(order.BackendKey);
        if (backend is null)
        {
            logger.LogWarning("Checkout order {OrderId} names unregistered backend {Backend}", order.Id, order.BackendKey);
            return false;
        }

        var status = await backend.GetStatusAsync(order.BackendOrderId, ct);
        var before = (order.State, order.RefundedMinor, order.FeeMinor);
        var newState = status.State switch
        {
            CheckoutState.Paid => TicketCheckoutOrderState.Paid,
            CheckoutState.Failed => TicketCheckoutOrderState.Failed,
            CheckoutState.Expired => TicketCheckoutOrderState.Expired,
            CheckoutState.Cancelled => TicketCheckoutOrderState.Cancelled,
            _ => TicketCheckoutOrderState.Pending,
        };

        // A paid order never goes back: a later backend state is a refund or a dispute, read as RefundedMinor.
        if (order.State == TicketCheckoutOrderState.Paid)
            newState = TicketCheckoutOrderState.Paid;

        if (newState == TicketCheckoutOrderState.Paid && order.State != TicketCheckoutOrderState.Paid)
            MarkPaid(order, status.FeeMinor, status.PaymentMethod, status.PaidAt ?? now);
        else
            order.State = newState;

        order.FeeMinor = status.FeeMinor ?? order.FeeMinor;
        order.PaymentMethod = status.PaymentMethod ?? order.PaymentMethod;
        order.RefundedMinor = status.RefundedMinor;

        if (before == (order.State, order.RefundedMinor, order.FeeMinor))
            return false;

        order.UpdatedAt = now;
        await repository.UpdateOrderAsync(order, ct);

        if (before.State != order.State)
        {
            var action = order.State == TicketCheckoutOrderState.Paid
                ? TicketsAuditActions.TicketCheckoutPaid
                : TicketsAuditActions.TicketCheckoutClosed;
            await auditLog.LogAsync(action, AuditEntityType, order.Id,
                $"{order.BackendKey} reports the order {order.State}: {FormatMinor(order.TotalMinor)} {order.Currency}" +
                (order.PaymentMethod is { } method ? $" by {method}" : string.Empty), JobName);
        }
        return true;
    }

    public async Task<CheckoutMutationResult> RefundAsync(
        Guid orderId, long amountMinor, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            return new("Give a reason of at most 500 characters.");
        if (amountMinor <= 0)
            return new("The refund amount must be positive.");

        var detail = await GetOrderDetailAsync(orderId, ct);
        if (detail is null)
            return new("That order does not exist.");
        var order = detail.Order;
        if (order.State != TicketCheckoutOrderState.Paid || order.BackendOrderId is null)
            return new("Only a paid order can be refunded.");
        // The guard Peter set for API refunds: tied to this payment, never above what its tickets cost.
        if (amountMinor > detail.RefundableMinor)
            return new($"At most {FormatMinor(detail.RefundableMinor)} {order.Currency} of this order's tickets can still be refunded.");
        var backend = BackendFor(order.BackendKey);
        if (backend is null)
            return new($"Backend '{order.BackendKey}' is not registered.");

        var refund = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = amountMinor,
            Reason = reason.Trim(),
            State = RefundState.Pending,
            RequestedByUserId = actorUserId,
            CreatedAt = clock.GetCurrentInstant(),
        };
        // Saved first, so a crash after the backend call still counts against what can be refunded.
        await repository.AddRefundAsync(refund, ct);

        RefundResult result;
        try
        {
            result = await backend.RefundAsync(
                new RefundRequest(refund.Id, order.BackendOrderId, amountMinor, refund.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Refund {RefundId} of order {OrderId} threw in {Backend}", refund.Id, order.Id, order.BackendKey);
            result = new RefundResult(RefundState.Failed, null, ex.Message);
        }

        refund.State = result.State;
        refund.BackendRefundId = result.BackendRefundId;
        refund.FailureReason = result.FailureReason;
        refund.ManualRefundUrl = result.ManualRefundUrl?.ToString();
        await repository.UpdateRefundAsync(refund, ct);

        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutRefunded, AuditEntityType, order.Id,
            $"Refund of {FormatMinor(amountMinor)} {order.Currency} through {order.BackendKey}: {result.State}" +
            (result.FailureReason is { } why ? $" ({why})" : string.Empty) + $". Reason: {refund.Reason}", actorUserId);

        if (result.State is RefundState.Succeeded or RefundState.Pending)
            await RefreshAsync(order, ct);

        return result.State == RefundState.Failed
            ? new($"The backend declined the refund: {result.FailureReason}")
            : CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> CancelAsync(Guid orderId, Guid userId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null || order.BuyerUserId != userId)
            return new("Tickets_Buy_OrderNotFound");
        if (order.State is not (TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending))
            return CheckoutMutationResult.Ok;

        if (order.BackendOrderId is not null && BackendFor(order.BackendKey) is { } backend)
        {
            // Paid in the meantime: the refresh records it instead of cancelling.
            await RefreshAsync(order, ct);
            if (order.State == TicketCheckoutOrderState.Paid)
                return new("Tickets_Buy_AlreadyPaid");
            await backend.CancelAsync(order.BackendOrderId, ct);
        }

        order.State = TicketCheckoutOrderState.Cancelled;
        order.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateOrderAsync(order, ct);
        return CheckoutMutationResult.Ok;
    }


    internal const string OrdersSlice = "TicketCheckoutOrders";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var orders = await repository.GetOrdersForUserAsync(userId, ct);
        return
        [
            new UserDataSlice(OrdersSlice, orders.Select(o => new
            {
                o.Id,
                PaidFor = o.BuyerUserId == userId,
                o.BuyerName,
                o.BuyerEmail,
                State = o.State.ToString(),
                Total = FormatMinor(o.TotalMinor),
                o.Currency,
                CreatedAt = o.CreatedAt.ToIso8601(),
                PaidAt = o.PaidAt.ToIso8601(),
                Refunded = FormatMinor(o.RefundedMinor),
                Lines = o.Lines
                    .Where(l => o.BuyerUserId == userId || l.AttendeeUserId == userId)
                    .Select(l => new { Kind = l.Kind.ToString(), l.Description, Amount = FormatMinor(l.AmountMinor), l.AttendeeName, l.AttendeeEmail })
                    .ToList(),
            }).ToList()),
        ];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [OrdersSlice] =
                "Partly retained: names and emails are replaced by the tombstone; amounts, dates and payment state stay, " +
                "keyed to the tombstoned id, as the association's accounting record of a sale (Código de Comercio " +
                "Art. 30, GDPR Art. 17(3)(b)).",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repository.ErasePiiForUserAsync(userId, ct);

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignAsync(mergedFromUserId, mergedToUserId, ct);

    private void MarkPaid(TicketCheckoutOrder order, long? feeMinor, string? method, Instant paidAt)
    {
        order.State = TicketCheckoutOrderState.Paid;
        order.PaidAt = paidAt;
        order.FeeMinor = feeMinor;
        order.PaymentMethod = method;
        foreach (var line in order.Lines.Where(l => l.Kind == CheckoutLineKind.Ticket && l.Barcode is null))
            line.Barcode = NewBarcode();
        logger.LogInformation("Checkout order {OrderId} paid", order.Id);
    }

    private sealed record BuyContext(
        TicketSalesEvent SalesEvent,
        EventSettingsInfo Event,
        UserInfo? Buyer,
        TicketPrice? OwnPrice,
        TicketPrice? PublicPrice,
        string? Unavailable,
        bool AlreadyHoldsTicket,
        IReadOnlyList<TicketCheckoutOrder> UserOrders,
        Instant Now);

    private async Task<BuyContext?> LoadBuyContextAsync(Guid userId, CancellationToken ct)
    {
        if (settings.GetActiveEventSettings() is not { } evt)
            return null;
        var salesEvent = await repository.GetSalesEventForEventAsync(evt.Id, ct);
        if (salesEvent is null)
            return null;

        var now = clock.GetCurrentInstant();
        var today = now.InZone(evt.TimeZone).Date;
        var buyer = await users.GetUserInfoAsync(userId, ct);
        var programIds = (await grants.GetActiveGrantsAsync(userId, ct))
            .Where(g => g.EventSettingsId is null || g.EventSettingsId == evt.Id)
            .Select(g => g.ProgramId)
            .ToHashSet();
        var ownPrice = TicketPrice.For(salesEvent, today, programIds);
        var publicPrice = TicketPrice.Public(salesEvent, today);
        var userOrders = await repository.GetOrdersForUserAsync(userId, ct);
        var holds = userOrders.Any(o => o.SalesEventId == salesEvent.Id && o.HoldsTickets(now)
            && o.Lines.Any(l => l.Kind == CheckoutLineKind.Ticket && l.AttendeeUserId == userId));

        string? unavailable = null;
        if (salesEvent.Mode == TicketSalesMode.Closed || (salesEvent.SalesCloseAt is { } close && now >= close))
            unavailable = "Tickets_Buy_Closed";
        else if (ownPrice is null || publicPrice is null)
            unavailable = "Tickets_Buy_NotOnSale";
        else if (BackendFor(salesEvent.BackendKey) is not { IsConfigured: true })
            unavailable = "Tickets_Buy_NotOnSale";
        else if (buyer is null || !buyer.HasRequiredNameFields || string.IsNullOrWhiteSpace(buyer.Email))
            unavailable = "Tickets_Buy_NeedLegalName";

        return new BuyContext(salesEvent, evt, buyer, ownPrice, publicPrice, unavailable, holds, userOrders, now);
    }

    private static TicketCheckoutOrderLine TicketLine(
        TicketSalesEvent salesEvent, TicketPrice price, string name, string email, Guid userId) => new()
    {
        Kind = CheckoutLineKind.Ticket,
        Description = price.ClassPrice is { } cls ? $"Ticket: {cls.Label}" : $"Ticket: {price.Wave.Name}",
        AmountMinor = price.AmountMinor,
        VatRate = salesEvent.TicketVatRate,
        AttendeeName = name,
        AttendeeEmail = email,
        AttendeeUserId = userId,
    };

    private static TicketCheckoutOrderLine DonationLine(long amountMinor, TicketDonationPurpose purpose) => new()
    {
        Kind = CheckoutLineKind.Donation,
        Description = purpose switch
        {
            TicketDonationPurpose.Creativity => "Donation: creativity",
            TicketDonationPurpose.LowIncomePool => "Donation: low income tickets",
            _ => "Donation",
        },
        AmountMinor = amountMinor,
        VatRate = 0m,
        DonationPurpose = purpose,
    };

    private static bool LooksLikeEmail(string? email) =>
        email is { Length: > 3 and <= 320 } && email.Contains('@', StringComparison.Ordinal) && !email.Contains(' ', StringComparison.Ordinal);

    /// <summary>Unambiguous Crockford base32, 16 characters: 80 bits, far beyond guessing at the gate.</summary>
    internal static string NewBarcode()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % 32];
        return "H" + new string(chars);
    }

    internal static string FormatMinor(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}
