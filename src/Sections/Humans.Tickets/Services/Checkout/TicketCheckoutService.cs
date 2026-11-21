using System.Globalization;
using System.Security.Cryptography;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Consent.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed partial class TicketCheckoutService(
    ITicketCheckoutRepository repository,
    IEnumerable<ITicketCheckoutBackend> backends,
    IGrantService grants,
    ISettingsService settings,
    IUserService users,
    IAccountProvisioningService provisioning,
    IUserEmailService userEmails,
    IConsentService consents,
    IConsentSubmission consentSubmission,
    ICommunicationPreferenceService preferenceReads,
    ICommunicationPreferenceWrite preferences,
    IEmailService email,
    TicketsEmails emails,
    IEnumerable<ITicketDelivery> deliveries,
    IStringLocalizer<TicketsResource> localizer,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<TicketCheckoutService> logger) : ITicketCheckoutService, IUserDataContributor, IUserMerge
{
    private const int MaxChildren = 6;
    private const int MaxNameLength = 513;
    private const int MaxEmailLength = 320;

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
        if (save.ChildPriceMinor < 0 || save.ChildMaxAge is <= 0 || save.ResaleOpensAtSold < 0 || save.DirectedResaleDays is < 1 or > 30)
            return new("Child price, child age, resale threshold and invitation days must be sensible numbers.");

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
            ChildPriceMinor = save.ChildPriceMinor,
            ChildMaxAge = save.ChildMaxAge,
            ResaleOpensAtSold = save.ResaleOpensAtSold,
            DirectedResaleDays = save.DirectedResaleDays,
            TermsTeamId = save.TermsTeamId,
            UpdatedAt = now,
        };
        await repository.SaveSalesEventAsync(salesEvent, ct);

        await auditLog.LogAsync(TicketsAuditActions.TicketSalesEventSaved, SalesEventAuditEntityType, salesEvent.Id,
            $"Sales setup saved: backend {salesEvent.BackendKey}, mode {salesEvent.Mode}, capacity {salesEvent.Capacity}; " +
            $"waves {string.Join(", ", salesEvent.Waves.Select(w => $"{w.Name} {FormatMinor(w.PriceMinor)} to {w.EndsOn.ToInvariantDate()}"))}; " +
            $"rules {string.Join(", ", salesEvent.PriceRules.Select(r => $"{r.Kind} {FormatMinor(r.AmountMinor)} for {r.GrantProgramId}"))}; " +
            $"supporter totals {string.Join(", ", salesEvent.SupporterTotalsMinor.Select(FormatMinor))}; " +
            $"child {(salesEvent.ChildPriceMinor is { } child ? FormatMinor(child) : "none")}", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<IReadOnlyList<BuyPage>> GetBuyPagesAsync(Guid userId, CancellationToken ct = default)
    {
        var pages = new List<(LocalDate GatesOpen, BuyPage Page)>();
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
        {
            if (salesEvent.Mode == TicketSalesMode.Closed)
                continue;
            if (await LoadBuyContextAsync(salesEvent.Id, userId, ct) is not { } context)
                continue;
            pages.Add((context.Event.GateOpeningDate, ToBuyPage(context, null, directed: false)));
        }
        return pages.OrderBy(p => p.GatesOpen).Select(p => p.Page).ToList();
    }

    public async Task<IReadOnlyList<PublicSale>> GetPublicSalesAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var sales = new List<(LocalDate GatesOpen, PublicSale Sale)>();
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
        {
            if (salesEvent.Mode == TicketSalesMode.Closed || (salesEvent.SalesCloseAt is { } close && now >= close)
                || BackendFor(salesEvent.BackendKey) is not { IsConfigured: true }
                || await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct) is not { } evt
                || TicketPrice.Public(salesEvent, now.InZone(evt.TimeZone).Date) is not { } price)
                continue;
            sales.Add((evt.GateOpeningDate,
                new PublicSale(evt.EventName, price.AmountMinor, salesEvent.Mode == TicketSalesMode.ResaleOnly)));
        }
        return sales.OrderBy(s => s.GatesOpen).Select(s => s.Sale).ToList();
    }

    public async Task<BuyPage?> GetDirectedBuyPageAsync(string token, Guid userId, CancellationToken ct = default)
    {
        var offer = await repository.GetOfferByTokenHashAsync(HashToken(token), ct);
        if (offer is null || await LoadBuyContextAsync(offer.SalesEventId, userId, ct) is not { } context)
            return null;
        return ToBuyPage(context, DirectedOfferFor(offer, context) is null ? "Tickets_Resale_InviteInvalid" : null, directed: true);
    }

    /// <summary>A returned ticket sells at the resale price, so a page that can only sell one shows that price.</summary>
    private static BuyPage ToBuyPage(BuyContext context, string? directedRefusal, bool directed) => new(
        context.SalesEvent, context.Event.EventName,
        directed || context.SalesEvent.Mode == TicketSalesMode.ResaleOnly ? context.ResalePrice : context.OwnPrice,
        context.PublicPrice,
        directedRefusal ?? context.Unavailable, context.AlreadyHoldsTicket, context.Buyer?.FullName ?? string.Empty,
        context.UserOrders.Where(o => o.SalesEventId == context.SalesEvent.Id).OrderByDescending(o => o.CreatedAt).ToList())
    {
        TermsToAccept = context.TermsToAccept,
        AskMarketing = context.AskMarketing,
    };

    public async Task<StartCheckoutResult> StartCheckoutAsync(
        StartCheckout command, Guid userId, CancellationToken ct = default)
    {
        var context = await LoadBuyContextAsync(command.SalesEventId, userId, ct);
        if (context is null)
            return StartCheckoutResult.Refused("Tickets_Buy_NotOnSale");
        if (context.Unavailable is { } unavailable)
            return StartCheckoutResult.Refused(unavailable);
        var buyer = context.Buyer!;
        var salesEvent = context.SalesEvent;
        var now = context.Now;

        // Every check that needs no partner account runs first, so a refused order leaves nothing behind.
        var hasPartner = !string.IsNullOrWhiteSpace(command.PartnerName) || !string.IsNullOrWhiteSpace(command.PartnerEmail);
        if (hasPartner && (string.IsNullOrWhiteSpace(command.PartnerName) || !LooksLikeEmail(command.PartnerEmail)))
            return StartCheckoutResult.Refused("Tickets_Buy_PartnerIncomplete");
        if (hasPartner && string.Equals(command.PartnerEmail!.Trim(), buyer.Email, StringComparison.OrdinalIgnoreCase))
            return StartCheckoutResult.Refused("Tickets_Buy_PartnerIsYou");
        if (hasPartner && (command.PartnerName!.Trim().Length > MaxNameLength || command.PartnerEmail!.Trim().Length > MaxEmailLength))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        if (command.ForSelf && context.AlreadyHoldsTicket)
            return StartCheckoutResult.Refused("Tickets_Buy_AlreadyHolds");
        if (command.DonationMinor < 0)
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        var children = command.ChildNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (children.Count > MaxChildren || children.Any(n => n.Length > MaxNameLength)
            || (children.Count > 0 && (!command.ForSelf || salesEvent.ChildPriceMinor is null)))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        if (command.SupporterTotalMinor is { } requested
            && (!command.ForSelf || !salesEvent.SupporterTotalsMinor.Contains(requested)))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        var unsignedTerms = context.TermsToAccept.Where(t => !t.Signed).ToList();
        if (unsignedTerms.Count > 0 && !command.AcceptTerms)
            return StartCheckoutResult.Refused("Tickets_Buy_TermsRequired");

        TicketResaleOffer? directed = null;
        if (command.DirectedToken is { } token)
        {
            var invited = await repository.GetOfferByTokenHashAsync(HashToken(token), ct);
            directed = invited is null ? null : DirectedOfferFor(invited, context);
            if (directed is null || !command.ForSelf)
                return StartCheckoutResult.Refused("Tickets_Resale_InviteInvalid");
        }

        var adults = (command.ForSelf ? 1 : 0) + (hasPartner ? 1 : 0);
        var stock = await LoadStockAsync(salesEvent, now, ct);
        var queue = stock.ResaleOpen
            ? new Queue<TicketResaleOffer>(stock.Offers
                .Where(o => o.State == TicketResaleOfferState.Open && !o.IsDirectedTo(now) && o.HolderUserId != buyer.Id)
                .OrderBy(o => o.OfferedAt))
            : new Queue<TicketResaleOffer>();
        // A returned ticket sells only at the resale price (current public wave, credits kept), so a buyer with a class
        // price or a pinned wave is served from fresh stock while there is any.
        var takesQueue = salesEvent.Mode == TicketSalesMode.ResaleOnly || !IsDiscounted(context)
            || stock.Held + 1 > salesEvent.Capacity;
        var resaleForSelf = command.ForSelf ? directed ?? (takesQueue && queue.Count > 0 ? queue.Dequeue() : null) : null;
        var selfPrice = resaleForSelf is null ? context.OwnPrice! : context.ResalePrice!;
        if (command.SupporterTotalMinor is { } total && total <= selfPrice.AmountMinor)
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        var resaleForPartner = hasPartner && queue.Count > 0 ? queue.Dequeue() : null;
        var fresh = (command.ForSelf && resaleForSelf is null ? 1 : 0) + (hasPartner && resaleForPartner is null ? 1 : 0) + children.Count;
        if (fresh > 0 && salesEvent.Mode != TicketSalesMode.Open)
            return StartCheckoutResult.Refused("Tickets_Buy_ResaleOnly");
        if (stock.Held + fresh > salesEvent.Capacity)
            return StartCheckoutResult.Refused("Tickets_Buy_SoldOut");

        var lines = new List<TicketCheckoutOrderLine>();
        if (command.ForSelf)
            lines.Add(TicketLine(salesEvent, selfPrice, buyer.FullName, buyer.Email!, buyer.Id, resaleForSelf?.Id));
        foreach (var child in children)
        {
            var line = ChildLine(salesEvent, child);
            line.ParentLineIndex = 0;
            lines.Add(line);
        }

        if (hasPartner)
        {
            // The partner's account is found or made only once the order is paid (ResolvePartnersAsync), so an
            // abandoned checkout leaves no account and nobody can probe an address for a ticket.
            lines.Add(TicketLine(salesEvent, context.PublicPrice!, command.PartnerName!.Trim(), command.PartnerEmail!.Trim(),
                null, resaleForPartner?.Id));
        }

        if (command.SupporterTotalMinor is { } supporterTotal)
            lines.Add(DonationLine(supporterTotal - selfPrice.AmountMinor, TicketDonationPurpose.General));
        if (command.DonationMinor > 0)
            lines.Add(DonationLine(command.DonationMinor, command.DonationPurpose));
        if (lines.Count == 0)
            return StartCheckoutResult.Refused("Tickets_Buy_Empty");

        foreach (var term in unsignedTerms)
        {
            var consent = await consentSubmission.SubmitConsentAsync(
                buyer.Id, term.DocumentVersionId, explicitConsent: true, command.IpAddress, command.UserAgent, ct);
            if (!consent.Success && !string.Equals(consent.ErrorKey, "AlreadyConsented", StringComparison.Ordinal))
                return StartCheckoutResult.Refused("Tickets_Buy_TermsFailed");
        }
        if (command.MarketingOptIn && context.AskMarketing)
            await preferences.UpdatePreferenceAsync(buyer.Id, MessageCategory.Marketing, optedOut: false, JobName, ct);

        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEvent.Id,
            BuyerUserId = buyer.Id,
            BuyerName = buyer.FullName,
            BuyerEmail = buyer.Email!,
            BackendKey = salesEvent.BackendKey,
            State = TicketCheckoutOrderState.Created,
            Culture = command.Culture,
            TotalMinor = lines.Sum(l => l.AmountMinor),
            CreatedAt = now,
            ExpiresAt = now + CheckoutLifetime,
            UpdatedAt = now,
            Lines = lines,
        };
        await repository.AddOrderAsync(order, ct);
        var ticketCount = lines.Count(l => l.Kind == CheckoutLineKind.Ticket);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.Created,
            $"{ticketCount} ticket(s), {FormatMinor(order.TotalMinor)} {order.Currency} on {order.BackendKey}" +
            (lines.Any(l => l.ResaleOfferId is not null) ? ", from the resale queue" : string.Empty), buyer.Id, ct);
        if (unsignedTerms.Count > 0)
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.TermsAccepted,
                "Accepted " + string.Join(", ", unsignedTerms.Select(t => t.Title)), buyer.Id, ct);
        foreach (var offer in new[] { resaleForSelf, resaleForPartner }.OfType<TicketResaleOffer>())
            await ReserveOfferAsync(offer, order.Id, ct);

        if (order.TotalMinor == 0)
        {
            // Nothing to charge (a free companion, credits covering the price): no backend has a zero checkout.
            MarkPaid(order, null, null, now);
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.StateChanged, "Paid: nothing to charge", null, ct);
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutPaid, AuditEntityType, order.Id,
                $"Free order issued {ticketCount} ticket(s) without a payment", buyer.Id);
            await CompletePaidAsync(order, ct);
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
                order.Lines.Select(l => new CheckoutLine(l.Kind, PaymentPageLabel(l), l.AmountMinor, l.VatRate, l.AttendeeName)).ToList(),
                order.ExpiresAt,
                new Uri(command.ReturnUrlBase, order.Id.ToString())), ct);

            order.BackendOrderId = session.BackendOrderId;
            order.PaymentUrl = session.PaymentUrl.ToString();
            order.AdminUrl = session.AdminUrl?.ToString();
            order.State = TicketCheckoutOrderState.Pending;
            order.UpdatedAt = clock.GetCurrentInstant();
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CheckoutOpened,
                $"{order.BackendKey} opened checkout {session.BackendOrderId}", null, ct);
            return new StartCheckoutResult(order.Id, session.PaymentUrl, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checkout backend {Backend} refused order {OrderId}", order.BackendKey, order.Id);
            order.State = TicketCheckoutOrderState.Failed;
            order.UpdatedAt = clock.GetCurrentInstant();
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.BackendRefused, Truncate(ex.Message), null, ct);
            await ReleaseOffersAsync(order, ct);
            return StartCheckoutResult.Refused("Tickets_Buy_BackendFailed");
        }
    }

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetOrdersForUserAsync(userId, ct)).OrderByDescending(o => o.CreatedAt).ToList();

    public async Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
        (await repository.GetOrdersForSalesEventAsync(salesEventId, ct)).OrderByDescending(o => o.CreatedAt).ToList();

    public async Task<CheckoutOrderDetail?> GetOrderDetailAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null)
            return null;
        var refunds = (await repository.GetRefundsAsync(orderId, ct)).OrderBy(r => r.CreatedAt).ToList();
        var timeline = (await repository.GetOrderEventsAsync(orderId, ct)).OrderBy(e => e.At).ToList();
        return new CheckoutOrderDetail(order, refunds, timeline, await GetTicketsForOrderAsync(orderId, ct));
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
        foreach (var orderId in await UnsettledRefundOrdersAsync(ct))
        {
            try
            {
                if (await repository.GetOrderAsync(orderId, ct) is { } paid && await RefreshAsync(paid, ct))
                    changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Refund settlement of checkout order {OrderId} failed; next run retries", orderId);
            }
        }
        changed += await RepairPaidAsync(ct);
        changed += await SweepOffersAsync(ct);
        return changed;
    }

    private async Task<IReadOnlyList<Guid>> UnsettledRefundOrdersAsync(CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
            ids.UnionWith((await repository.GetRefundsForSalesEventAsync(salesEvent.Id, ct))
                .Where(r => r.State is RefundState.Pending or RefundState.ManualRequired)
                .Select(r => r.OrderId));
        return ids.ToList();
    }

    /// <summary>True when the state moved or a refund settled.</summary>
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
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.StateChanged, "Expired: never reached the backend", null, ct);
            await ReleaseOffersAsync(order, ct);
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

        var wasOpen = order.State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending;
        if (wasOpen && newState == TicketCheckoutOrderState.Pending && order.ExpiresAt <= now)
        {
            // Tickets owns the hold: once it lapses the backend must stop taking money for it.
            if (await TryCancelAtBackendAsync(backend, order, ct))
                newState = TicketCheckoutOrderState.Expired;
        }
        else if (wasOpen && newState is TicketCheckoutOrderState.Expired or TicketCheckoutOrderState.Failed)
        {
            await TryCancelAtBackendAsync(backend, order, ct);
        }

        var becamePaid = newState == TicketCheckoutOrderState.Paid && order.State != TicketCheckoutOrderState.Paid;
        var late = becamePaid && !wasOpen;
        if (becamePaid)
        {
            // The webhook, the return page and the reconcile job can all see the same payment at once: only the
            // caller whose conditional update moves the row to Paid goes on to fulfil it.
            if (!await repository.TryClaimPaidAsync(order.Id, status.PaidAt ?? now, ct))
                return false;
            MarkPaid(order, status.FeeMinor, status.PaymentMethod, status.PaidAt ?? now);
        }
        else
        {
            order.State = newState;
        }

        order.FeeMinor = status.FeeMinor ?? order.FeeMinor;
        order.PaymentMethod = status.PaymentMethod ?? order.PaymentMethod;
        order.RefundedMinor = status.RefundedMinor;

        var refundsSettled = order.State == TicketCheckoutOrderState.Paid && await SettleRefundsAsync(order, status, ct);
        if (before == (order.State, order.RefundedMinor, order.FeeMinor))
            return refundsSettled;

        order.UpdatedAt = now;
        await repository.UpdateOrderAsync(order, ct);

        if (before.State != order.State)
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.StateChanged,
                $"{order.BackendKey} reports {order.State}" +
                (order.PaymentMethod is { } paidBy && order.State == TicketCheckoutOrderState.Paid ? $" by {paidBy}" : string.Empty) +
                (order.FeeMinor is { } fee ? $", fee {FormatMinor(fee)}" : string.Empty) +
                (late ? $", after the order was {before.State}: the ticketing team should check capacity" : string.Empty), null, ct);
        if (before.RefundedMinor != order.RefundedMinor)
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundedTotalChanged,
                $"{order.BackendKey} reports {FormatMinor(order.RefundedMinor)} {order.Currency} refunded in total", null, ct);

        if (late)
            logger.LogWarning("Checkout order {OrderId} was paid after it was {State}; its tickets are issued anyway",
                order.Id, before.State);
        if (becamePaid)
            await CompletePaidAsync(order, ct);
        else if (before.State != order.State && order.State is not TicketCheckoutOrderState.Pending)
            await ReleaseOffersAsync(order, ct);

        if (before.State != order.State)
        {
            var action = order.State == TicketCheckoutOrderState.Paid
                ? TicketsAuditActions.TicketCheckoutPaid
                : TicketsAuditActions.TicketCheckoutClosed;
            await auditLog.LogAsync(action, AuditEntityType, order.Id,
                $"{order.BackendKey} reports the order {order.State}: {FormatMinor(order.TotalMinor)} {order.Currency}" +
                (order.PaymentMethod is { } method ? $" by {method}" : string.Empty) +
                (late ? $" after it was {before.State}" : string.Empty), JobName);
        }
        return true;
    }

    /// <summary>False when the backend refused, which means it was paid meanwhile: the next read records that.</summary>
    private async Task<bool> TryCancelAtBackendAsync(ITicketCheckoutBackend backend, TicketCheckoutOrder order, CancellationToken ct)
    {
        try
        {
            await backend.CancelAsync(order.BackendOrderId!, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Closing checkout order {OrderId} at {Backend} failed", order.Id, order.BackendKey);
            return false;
        }
    }

    /// <summary>
    /// Settles pending and manual refunds from the backend's refunded total, oldest first; when the backend reports a
    /// failed refund, the ones that total does not cover failed. True when any changed.
    /// </summary>
    private async Task<bool> SettleRefundsAsync(TicketCheckoutOrder order, CheckoutStatus status, CancellationToken ct)
    {
        var refunds = (await repository.GetRefundsAsync(order.Id, ct)).OrderBy(r => r.CreatedAt).ToList();
        var covered = status.RefundedMinor - refunds.Where(r => r.State == RefundState.Succeeded).Sum(r => r.AmountMinor);
        var changed = false;
        foreach (var refund in refunds.Where(r => r.State is RefundState.Pending or RefundState.ManualRequired))
        {
            if (refund.AmountMinor <= covered)
            {
                covered -= refund.AmountMinor;
                refund.State = RefundState.Succeeded;
            }
            else if (status.RefundFailed)
            {
                refund.State = RefundState.Failed;
                refund.FailureReason = $"{order.BackendKey} reports the refund failed";
            }
            else
            {
                continue;
            }
            await repository.UpdateRefundAsync(refund, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundSettled,
                $"Refund of {FormatMinor(refund.AmountMinor)} {order.Currency} {refund.State} per {order.BackendKey}", null, ct);
            changed = true;
        }
        return changed;
    }

    public async Task<CheckoutMutationResult> RefundAsync(
        Guid orderId, long amountMinor, string reason, Guid actorUserId, CancellationToken ct = default) =>
        (await RefundCoreAsync(orderId, amountMinor, reason, actorUserId, ct)).Result;

    /// <summary>
    /// <paramref name="actorUserId"/> is null for automation: the refund owed when a returned ticket resells, which
    /// passes the offer id as <paramref name="refundId"/> so a repeat finds the same refund instead of a second one.
    /// </summary>
    private async Task<(CheckoutMutationResult Result, Guid? RefundId)> RefundCoreAsync(
        Guid orderId, long amountMinor, string reason, Guid? actorUserId, CancellationToken ct, Guid? refundId = null)
    {
        if (refundId is { } existingId
            && (await repository.GetRefundsAsync(orderId, ct)).FirstOrDefault(r => r.Id == existingId) is { } existing)
        {
            if (existing is { State: RefundState.Pending, BackendRefundId: null }
                && await repository.GetOrderAsync(orderId, ct) is { BackendOrderId: not null } paidOrder
                && BackendFor(paidOrder.BackendKey) is { } retryBackend)
                await CallBackendRefundAsync(retryBackend, paidOrder, existing, actorUserId, ct);
            return (CheckoutMutationResult.Ok, existing.Id);
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            return (new("Give a reason of at most 500 characters."), null);
        if (amountMinor <= 0)
            return (new("The refund amount must be positive."), null);

        var detail = await GetOrderDetailAsync(orderId, ct);
        if (detail is null)
            return (new("That order does not exist."), null);
        var order = detail.Order;
        if (order.State != TicketCheckoutOrderState.Paid || order.BackendOrderId is null)
            return (new("Only a paid order can be refunded."), null);
        // The guard Peter set for API refunds: tied to this payment, never above what its tickets cost.
        if (amountMinor > detail.RefundableMinor)
            return (new($"At most {FormatMinor(detail.RefundableMinor)} {order.Currency} of this order's tickets can still be refunded."), null);
        var backend = BackendFor(order.BackendKey);
        if (backend is null)
            return (new($"Backend '{order.BackendKey}' is not registered."), null);

        var refund = new TicketCheckoutRefund
        {
            Id = refundId ?? Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = amountMinor,
            Reason = reason.Trim(),
            State = RefundState.Pending,
            RequestedByUserId = actorUserId,
            CreatedAt = clock.GetCurrentInstant(),
        };
        // Saved first, so a crash after the backend call still counts against what can be refunded.
        await repository.AddRefundAsync(refund, ct);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundRequested,
            $"Refund of {FormatMinor(amountMinor)} {order.Currency} requested: {Truncate(refund.Reason)}", actorUserId, ct);

        var result = await CallBackendRefundAsync(backend, order, refund, actorUserId, ct);
        return (result.State == RefundState.Failed
            ? new($"The backend declined the refund: {result.FailureReason}")
            : CheckoutMutationResult.Ok, refund.Id);
    }

    private async Task<RefundResult> CallBackendRefundAsync(
        ITicketCheckoutBackend backend, TicketCheckoutOrder order, TicketCheckoutRefund refund, Guid? actorUserId, CancellationToken ct)
    {
        var amountMinor = refund.AmountMinor;
        RefundResult result;
        try
        {
            result = await backend.RefundAsync(
                new RefundRequest(refund.Id, order.BackendOrderId!, amountMinor, refund.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A timeout leaves the outcome unknown and the money may have gone: the status read settles it.
            logger.LogError(ex, "Refund {RefundId} of order {OrderId} threw in {Backend}", refund.Id, order.Id, order.BackendKey);
            result = new RefundResult(RefundState.Pending, null, ex.Message);
        }

        refund.State = result.State;
        refund.BackendRefundId = result.BackendRefundId;
        refund.FailureReason = result.FailureReason;
        refund.ManualRefundUrl = result.ManualRefundUrl?.ToString();
        await repository.UpdateRefundAsync(refund, ct);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundSettled,
            $"{order.BackendKey} answered {result.State}" + (result.BackendRefundId is { } refundRef ? $" ({refundRef})" : string.Empty) +
            (result.FailureReason is { } failure ? $": {Truncate(failure)}" : string.Empty), null, ct);

        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutRefunded, AuditEntityType, order.Id,
            $"Refund of {FormatMinor(amountMinor)} {order.Currency} through {order.BackendKey}: {result.State}" +
            (result.FailureReason is { } why ? $" ({why})" : string.Empty) + $". Reason: {refund.Reason}",
            actorUserId?.ToString() ?? JobName);

        if (result.State is RefundState.Succeeded or RefundState.Pending)
            await RefreshAsync(order, ct);
        return result;
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
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.Cancelled, "Abandoned by the buyer", userId, ct);
        await ReleaseOffersAsync(order, ct);
        return CheckoutMutationResult.Ok;
    }


    internal const string OrdersSlice = "TicketCheckoutOrders";
    internal const string TicketsSlice = "TicketCheckoutTickets";
    internal const string OffersSlice = "TicketResaleOffers";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var orders = await repository.GetOrdersForUserAsync(userId, ct);
        var held = await repository.GetTicketsForHolderAsync(userId, ct);
        var tickets = held.Concat(await ChildrenOfAsync(held, ct)).ToList();
        var offers = new List<TicketResaleOffer>();
        foreach (var salesEventId in tickets.Select(t => t.SalesEventId).Distinct())
            offers.AddRange((await repository.GetOffersForSalesEventAsync(salesEventId, ct)).Where(o => o.HolderUserId == userId));
        return
        [
            new UserDataSlice(OrdersSlice, orders.Select(o => new
            {
                o.Id,
                PaidFor = o.BuyerUserId == userId,
                // A partner sees their own line, not who paid or what the whole order cost.
                BuyerName = o.BuyerUserId == userId ? o.BuyerName : null,
                BuyerEmail = o.BuyerUserId == userId ? o.BuyerEmail : null,
                State = o.State.ToString(),
                Total = o.BuyerUserId == userId ? FormatMinor(o.TotalMinor) : null,
                o.Currency,
                CreatedAt = o.CreatedAt.ToIso8601(),
                PaidAt = o.PaidAt.ToIso8601(),
                Refunded = o.BuyerUserId == userId ? FormatMinor(o.RefundedMinor) : null,
                Lines = o.Lines
                    .Where((l, i) => o.BuyerUserId == userId || l.AttendeeUserId == userId
                        || (l.ParentLineIndex is { } parent && o.Lines[parent].AttendeeUserId == userId))
                    .Select(l => new { Kind = l.Kind.ToString(), l.Description, Amount = FormatMinor(l.AmountMinor), l.AttendeeName, l.AttendeeEmail })
                    .ToList(),
            }).ToList()),
            new UserDataSlice(TicketsSlice, tickets.Select(t => new
            {
                t.Id,
                t.OrderId,
                Kind = t.Kind.ToString(),
                t.HolderName,
                t.HolderEmail,
                t.Barcode,
                PricePaid = FormatMinor(t.PricePaidMinor),
                t.WaveName,
                t.ClassLabel,
                State = t.State.ToString(),
                IssuedAt = t.IssuedAt.ToIso8601(),
                VoidedAt = t.VoidedAt.ToIso8601(),
                t.VoidReason,
            }).ToList()),
            new UserDataSlice(OffersSlice, offers.Select(o => new
            {
                o.Id,
                o.TicketId,
                State = o.State.ToString(),
                OfferedAt = o.OfferedAt.ToIso8601(),
                RefundOwed = FormatMinor(o.RefundOwedMinor),
                SoldAt = o.SoldAt.ToIso8601(),
                WithdrawnAt = o.WithdrawnAt.ToIso8601(),
                o.DirectedEmail,
                DirectedExpiresAt = o.DirectedExpiresAt.ToIso8601(),
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
            [TicketsSlice] =
                "Partly retained: holder name and email, and the names on their children's tickets, are replaced by the " +
                "tombstone, and live tickets are voided; barcode, price paid and state stay as the accounting record of " +
                "the sale and so the code can never scan again.",
            [OffersSlice] =
                "Partly retained: the invited buyer's email and the invitation are cleared; dates, state and the refund " +
                "owed stay as the accounting record of the resale.",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The backends' copies go first: once Tickets' own copy is tombstoned the emails to erase are gone, so a backend
    /// failure throws before anything here changes and the whole erasure retries.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var orders = await repository.GetOrdersForUserAsync(userId, ct);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if ((await users.GetUserInfoAsync(userId, ct))?.Email is { } accountEmail)
            addresses.Add(accountEmail);
        foreach (var order in orders)
        {
            var people = order.Lines.Where(l => l.AttendeeUserId == userId).Select(l => l.AttendeeEmail)
                .Append(order.BuyerUserId == userId ? order.BuyerEmail : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            addresses.UnionWith(people);
            if (order.BackendOrderId is null)
                continue;
            if (BackendFor(order.BackendKey) is not { } backend)
            {
                // A retired backend's data must be erased or exported before its adapter is removed; this only
                // keeps one leftover order from blocking every later deletion.
                logger.LogWarning("Order {OrderId} is on retired backend {Backend}; its copy there was not erased", order.Id, order.BackendKey);
                await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutClosed, AuditEntityType, order.Id,
                    $"GDPR erasure could not reach retired backend {order.BackendKey}", JobName);
                continue;
            }
            foreach (var personEmail in people)
                await backend.ErasePersonalDataAsync(order.BackendOrderId, personEmail, ct);
        }

        var now = clock.GetCurrentInstant();
        var held = await repository.GetTicketsForHolderAsync(userId, ct);
        foreach (var ticket in held.Concat(await ChildrenOfAsync(held, ct))
                     .Where(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered))
        {
            ticket.State = TicketCheckoutTicketState.Void;
            ticket.VoidedAt = now;
            ticket.VoidReason = "Holder's personal data erased";
            await repository.UpdateTicketAsync(ticket, ct);
        }
        foreach (var salesEventId in held.Select(t => t.SalesEventId).Distinct())
            foreach (var offer in (await repository.GetOffersForSalesEventAsync(salesEventId, ct))
                         .Where(o => o.HolderUserId == userId && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved))
            {
                offer.State = TicketResaleOfferState.Withdrawn;
                offer.WithdrawnAt = now;
                await repository.UpdateOfferAsync(offer, ct);
            }
        await repository.ErasePiiForUserAsync(userId, addresses, ct);
    }

    private async Task<IReadOnlyList<TicketCheckoutTicket>> ChildrenOfAsync(
        IReadOnlyList<TicketCheckoutTicket> adults, CancellationToken ct)
    {
        var ids = adults.Select(t => t.Id).ToHashSet();
        var children = new List<TicketCheckoutTicket>();
        foreach (var salesEventId in adults.Select(t => t.SalesEventId).Distinct())
            children.AddRange((await repository.GetTicketsForSalesEventAsync(salesEventId, ct))
                .Where(t => t.ParentTicketId is { } parent && ids.Contains(parent) && t.HolderUserId is null));
        return children;
    }

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignAsync(mergedFromUserId, mergedToUserId, ct);

    private Task TimelineAsync(
        Guid orderId, TicketCheckoutOrderEventKind kind, string detail, Guid? actorUserId, CancellationToken ct) =>
        repository.AddOrderEventAsync(new TicketCheckoutOrderEvent
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            At = clock.GetCurrentInstant(),
            Kind = kind,
            Detail = Truncate(detail, 2000),
            ActorUserId = actorUserId,
        }, ct);

    private static string Truncate(string text, int max = 500) => text.Length <= max ? text : text[..max];

    private void MarkPaid(TicketCheckoutOrder order, long? feeMinor, string? method, Instant paidAt)
    {
        order.State = TicketCheckoutOrderState.Paid;
        order.PaidAt = paidAt;
        order.FeeMinor = feeMinor;
        order.PaymentMethod = method;
        logger.LogInformation("Checkout order {OrderId} paid", order.Id);
    }

    private sealed record BuyContext(
        TicketSalesEvent SalesEvent,
        EventSettingsInfo Event,
        UserInfo? Buyer,
        TicketPrice? OwnPrice,
        TicketPrice? PublicPrice,
        TicketPrice? ResalePrice,
        string? Unavailable,
        bool AlreadyHoldsTicket,
        IReadOnlyList<TicketCheckoutOrder> UserOrders,
        IReadOnlyList<RequiredConsentRow> TermsToAccept,
        bool AskMarketing,
        Instant Now);

    private async Task<BuyContext?> LoadBuyContextAsync(Guid salesEventId, Guid userId, CancellationToken ct)
    {
        if (await repository.GetSalesEventAsync(salesEventId, ct) is not { } salesEvent)
            return null;
        if (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct) is not { } evt)
            return null;

        var now = clock.GetCurrentInstant();
        var zone = evt.TimeZone;
        var today = now.InZone(zone).Date;
        var buyer = await users.GetUserInfoAsync(userId, ct);
        var activeGrants = (await grants.GetActiveGrantsAsync(userId, ct))
            .Where(g => g.EventSettingsId is null || g.EventSettingsId == evt.Id)
            .ToList();
        var programIds = activeGrants.Select(g => g.ProgramId).ToHashSet();
        var pinPrograms = salesEvent.PriceRules.Where(r => r.Kind == TicketPriceRuleKind.WavePin).Select(r => r.GrantProgramId).ToHashSet();
        var pinnedOn = activeGrants.Where(g => pinPrograms.Contains(g.ProgramId))
            .Select(g => (LocalDate?)g.IssuedAt.InZone(zone).Date)
            .Min();
        var ownPrice = TicketPrice.For(salesEvent, today, programIds, pinnedOn);
        var publicPrice = TicketPrice.Public(salesEvent, today);
        var creditPrograms = salesEvent.PriceRules.Where(r => r.Kind == TicketPriceRuleKind.Credit)
            .Select(r => r.GrantProgramId).Where(programIds.Contains).ToHashSet();
        var resalePrice = TicketPrice.For(salesEvent, today, creditPrograms);
        var userOrders = await repository.GetOrdersForUserAsync(userId, ct);
        var holds = await HoldsTicketAsync(salesEvent.Id, userId, now, ct);
        var terms = salesEvent.TermsTeamId is { } termsTeam
            ? await consents.GetRequiredConsentRowsForUserAsync(userId, termsTeam, ct)
            : [];
        var askMarketing = await preferenceReads.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, ct) is null;

        string? unavailable = null;
        if (salesEvent.Mode == TicketSalesMode.Closed || (salesEvent.SalesCloseAt is { } close && now >= close))
            unavailable = "Tickets_Buy_Closed";
        else if (ownPrice is null || publicPrice is null)
            unavailable = "Tickets_Buy_NotOnSale";
        else if (BackendFor(salesEvent.BackendKey) is not { IsConfigured: true })
            unavailable = "Tickets_Buy_NotOnSale";
        else if (buyer is null || !buyer.HasRequiredNameFields || string.IsNullOrWhiteSpace(buyer.Email))
            unavailable = "Tickets_Buy_NeedLegalName";

        return new BuyContext(salesEvent, evt, buyer, ownPrice, publicPrice, resalePrice, unavailable, holds, userOrders, terms,
            askMarketing, now);
    }

    /// <summary>The line's name on the payment page, in the buyer's language (the request culture at checkout).</summary>
    private string PaymentPageLabel(TicketCheckoutOrderLine line) => line.Kind == CheckoutLineKind.Donation
        ? string.Format(CultureInfo.CurrentCulture, localizer["Tickets_Buy_Line_Donation"].Value,
            localizer[$"Tickets_Buy_Purpose_{line.DonationPurpose ?? TicketDonationPurpose.General}"].Value)
        : line.HolderKind == TicketHolderKind.Child
            ? localizer["Tickets_Buy_Line_Child"].Value
            : $"{localizer["Tickets_Buy_Line_Ticket"].Value} ({line.ClassLabel ?? line.WaveName})";

    private static bool IsDiscounted(BuyContext context) =>
        context.OwnPrice!.ClassPrice is not null || !ReferenceEquals(context.OwnPrice.Wave, context.PublicPrice!.Wave);

    /// <summary>An adult ticket held, offered for resale, or on an unpaid checkout that has not lapsed.</summary>
    private async Task<bool> HoldsTicketAsync(Guid salesEventId, Guid userId, Instant now, CancellationToken ct)
    {
        if ((await repository.GetTicketsForHolderAsync(userId, ct)).Any(t =>
                t.SalesEventId == salesEventId && t.Kind == TicketHolderKind.Adult
                && t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered))
            return true;
        return (await repository.GetOrdersForUserAsync(userId, ct)).Any(o =>
            o.SalesEventId == salesEventId && o.State != TicketCheckoutOrderState.Paid && o.HoldsTickets(now)
            && o.Lines.Any(l => l.Kind == CheckoutLineKind.Ticket && l.AttendeeUserId == userId && l.HolderKind != TicketHolderKind.Child));
    }

    /// <summary>
    /// <paramref name="Held"/> counts against capacity: issued tickets still valid or offered, and fresh-stock lines on
    /// unpaid checkouts that have not lapsed. A resold ticket replaces the one it was offered as, so it adds nothing.
    /// </summary>
    private sealed record Stock(int Held, int Sold, bool ResaleOpen, IReadOnlyList<TicketResaleOffer> Offers);

    private async Task<Stock> LoadStockAsync(TicketSalesEvent salesEvent, Instant now, CancellationToken ct)
    {
        var sold = (await repository.GetTicketsForSalesEventAsync(salesEvent.Id, ct))
            .Count(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered);
        var pending = (await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct))
            .Where(o => o.State != TicketCheckoutOrderState.Paid && o.HoldsTickets(now))
            .Sum(o => o.Lines.Count(l => l.Kind == CheckoutLineKind.Ticket && l.ResaleOfferId is null));
        var resaleOpen = salesEvent.Mode != TicketSalesMode.Closed && sold >= (salesEvent.ResaleOpensAtSold ?? 0);
        return new Stock(sold + pending, sold, resaleOpen, await repository.GetOffersForSalesEventAsync(salesEvent.Id, ct));
    }

    private static TicketCheckoutOrderLine TicketLine(
        TicketSalesEvent salesEvent, TicketPrice price, string name, string email, Guid? userId, Guid? resaleOfferId) => new()
        {
            Kind = CheckoutLineKind.Ticket,
            HolderKind = TicketHolderKind.Adult,
            Description = price.ClassPrice is { } cls ? $"Ticket: {cls.Label}" : $"Ticket: {price.Wave.Name}",
            AmountMinor = price.AmountMinor,
            VatRate = salesEvent.TicketVatRate,
            AttendeeName = name,
            AttendeeEmail = email,
            AttendeeUserId = userId,
            WaveName = price.Wave.Name,
            ClassProgramId = price.ClassPrice?.GrantProgramId,
            ClassLabel = price.ClassPrice?.Label,
            CreditsMinor = price.Credits.Sum(c => c.AmountMinor),
            ResaleOfferId = resaleOfferId,
        };

    private static TicketCheckoutOrderLine ChildLine(TicketSalesEvent salesEvent, string name) => new()
    {
        Kind = CheckoutLineKind.Ticket,
        HolderKind = TicketHolderKind.Child,
        Description = "Child ticket",
        AmountMinor = salesEvent.ChildPriceMinor!.Value,
        VatRate = salesEvent.TicketVatRate,
        AttendeeName = name,
        WaveName = "Child",
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
