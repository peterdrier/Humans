using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Gate.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Stores;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed partial class TicketCheckoutService(
    ITicketCheckoutRepository repository,
    IEnumerable<ITicketingPlatform> platforms,
    IGrantService grants,
    ISettingsService settings,
    IUserService users,
    IAccountProvisioningService provisioning,
    IUserEmailService userEmails,
    ICommunicationPreferenceService preferenceReads,
    ICommunicationPreferenceWrite preferences,
    IEmailService email,
    TicketsEmails emails,
    IEnumerable<ITicketDelivery> deliveries,
    IGateAdmissions gateAdmissions,
    ITicketRulesSource rulesSource,
    IStringLocalizer<TicketsResource> localizer,
    IAuditLogService auditLog,
    IClock clock,
    CachingTicketQueryService holdings,
    ILogger<TicketCheckoutService> logger) : ITicketCheckoutService, IUserDataContributor, IUserMerge
{
    private const int MaxChildren = 6;
    private const int MaxOthers = 10;
    private const int MaxNameLength = 513;

    /// <summary>Child type names and donation purposes, as the buyer sees them.</summary>
    private const int MaxTypeNameLength = 64;
    private const int MaxEmailLength = 320;

    internal const string AuditEntityType = "TicketCheckoutOrder";
    // Stored on audit rows, so it keeps the name from before the glossary.
    internal const string TicketingSettingsAuditEntityType = "TicketSalesEvent";
    private const string JobName = "TicketCheckout";

    /// <summary>Long enough to pay with a bank redirect; Stripe accepts 30 minutes to 24 hours.</summary>
    internal static readonly Duration CheckoutLifetime = Duration.FromHours(1);

    private readonly IReadOnlyList<ITicketingPlatform> _platforms = platforms.ToList();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_ ./-]*$", RegexOptions.None, 1000)]
    private static partial Regex TermsFolderPattern();

    private ITicketingPlatform? PlatformFor(string key) =>
        _platforms.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));

    public Task<IReadOnlyList<TicketingSettings>> ListTicketingSettingsAsync(CancellationToken ct = default) =>
        repository.ListTicketingSettingsAsync(ct);

    public Task<TicketingSettings?> GetTicketingSettingsAsync(Guid id, CancellationToken ct = default) =>
        repository.GetTicketingSettingsAsync(id, ct);

    public async Task<CheckoutMutationResult> SaveTicketingSettingsAsync(
        TicketingSettingsSave save, Guid actorUserId, CancellationToken ct = default)
    {
        if (await settings.GetEventSettingsByIdAsync(save.EventSettingsId, ct) is null)
            return new("Pick an existing event.");
        if (PlatformFor(save.PlatformKey) is null)
            return new($"No ticketing platform is registered under '{save.PlatformKey}'.");
        if (save.Capacity < 0)
            return new("Capacity cannot be negative.");
        if (save.TicketVatRate is < 0 or >= 1)
            return new("Ticket VAT is a percentage from 0 to under 100.");
        if (save.Waves.Any(w => w.PriceMinor < 0) || save.Waves.GroupBy(w => w.EndsOn).Any(g => g.Count() > 1))
            return new("Waves need distinct end dates and non-negative prices.");
        if (save.PriceRules.Any(r => r.AmountMinor < 0))
            return new("Price rule amounts cannot be negative.");
        if (save.VipTotalsMinor.Any(t => t <= 0))
            return new("VIP totals must be positive.");
        if (save.ChildTypes.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > MaxTypeNameLength || c.UnderAge <= 0 || c.PriceMinor < 0)
            || save.ChildTypes.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)
            || save.ChildTypes.GroupBy(c => c.UnderAge).Any(g => g.Count() > 1))
            return new($"Child tickets need distinct names of at most {MaxTypeNameLength} characters, distinct ages above zero and non-negative prices.");
        if (save.DonationPurposes.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > MaxTypeNameLength)
            || save.DonationPurposes.GroupBy(p => p.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return new($"Donation purposes need distinct names of at most {MaxTypeNameLength} characters.");
        if (save.Mode == TicketSalesMode.ResaleOnly && save.Switches.Resale == ResaleScheme.Off)
            return new("Resale only needs resale switched on.");
        if (save.Mode == TicketSalesMode.InvitedOnly && save.PriceRules.All(r => r.Kind != TicketPriceRuleKind.EarlyAccess))
            return new("Invited only needs an early access price rule naming the grant program that is invited.");
        if (save.TermsFolder is { } folder && (folder.Length > 200 || !TermsFolderPattern().IsMatch(folder) || folder.Contains("..", StringComparison.Ordinal)))
            return new("The ticket rules folder is a path in the legal repo, such as TicketRules/2027, of at most 200 characters.");

        var existing = save.Id is { } id ? await repository.GetTicketingSettingsAsync(id, ct) : null;
        if (save.Id is not null && existing is null)
            return new("Those ticketing settings no longer exist.");
        if (existing?.CancelledAt is not null)
            return new("The event is cancelled; its ticketing settings can no longer change.");
        if (save.TermsFolder is { } newFolder && !string.Equals(newFolder, existing?.TermsFolder, StringComparison.Ordinal)
            && await rulesSource.GetAsync(newFolder, refresh: true, ct) is { Problem: { } problem })
            return new($"The ticket rules in {newFolder} can't be used. {problem} " +
                "The folder needs one name.md (Spanish) and name-en.md (English), and no other markdown without a language suffix.");
        var clash = await repository.GetTicketingSettingsForEventAsync(save.EventSettingsId, ct);
        if (clash is not null && clash.Id != existing?.Id)
            return new("That event already has ticketing settings.");

        var now = clock.GetCurrentInstant();
        var ticketing = new TicketingSettings
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            CreatedAt = existing?.CreatedAt ?? now,
            EventSettingsId = save.EventSettingsId,
            PlatformKey = save.PlatformKey,
            PlatformEventRef = string.IsNullOrWhiteSpace(save.PlatformEventRef) ? null : save.PlatformEventRef.Trim(),
            Mode = save.Mode,
            SalesCloseAt = save.SalesCloseAt,
            Capacity = save.Capacity,
            TicketVatRate = save.TicketVatRate,
            Waves = save.Waves.OrderBy(w => w.EndsOn).ToList(),
            VipTotalsMinor = save.VipTotalsMinor.Distinct().Order().ToList(),
            PriceRules = save.PriceRules.ToList(),
            ChildTypes = save.ChildTypes
                .Select(c => new TicketChildType { Name = c.Name.Trim(), UnderAge = c.UnderAge, PriceMinor = c.PriceMinor })
                .OrderBy(c => c.UnderAge).ToList(),
            DonationPurposes = save.DonationPurposes.Select(p => p.Trim()).ToList(),
            ResaleOpenedAt = save.Switches.Resale == ResaleScheme.Off ? null : existing?.ResaleOpenedAt,
            Switches = save.Switches,
            TermsFolder = save.TermsFolder,
            UpdatedAt = now,
        };
        await repository.SaveTicketingSettingsAsync(ticketing, ct);
        if (ticketing.EffectiveSwitches.Resale == ResaleScheme.Off)
            await WithdrawOpenOffersAsync(ticketing.Id, actorUserId, ct);
        else if (ticketing.EffectiveSwitches.Resale != ResaleScheme.QueueAndDirected)
            await EndInvitationsAsync(ticketing.Id, actorUserId, ct);

        await auditLog.LogAsync(TicketsAuditActions.TicketSalesEventSaved, TicketingSettingsAuditEntityType, ticketing.Id,
            $"Ticketing settings saved: platform {ticketing.PlatformKey}, mode {ticketing.Mode}, capacity {ticketing.Capacity}; " +
            $"waves {string.Join(", ", ticketing.Waves.Select(w => $"{w.Name} {FormatMinor(w.PriceMinor)} to {w.EndsOn.ToInvariantDate()}"))}; " +
            $"rules {string.Join(", ", ticketing.PriceRules.Select(r => $"{r.Kind} {FormatMinor(r.AmountMinor)} for {r.GrantProgramId}"))}; " +
            $"VIP totals {string.Join(", ", ticketing.VipTotalsMinor.Select(FormatMinor))}; " +
            $"child tickets {(ticketing.ChildTypes.Count == 0 ? "none" : string.Join(", ", ticketing.ChildTypes.Select(c => $"{c.Name} under {c.UnderAge} {FormatMinor(c.PriceMinor)}")))}; " +
            $"donation purposes {(ticketing.DonationPurposes is not [_, ..] ? "general only" : string.Join(", ", ticketing.DonationPurposes))}; " +
            $"ticket rules {ticketing.TermsFolder ?? "none"}; resale {save.Switches.Resale}" +
            (existing?.ResaleOpenedAt is not null && ticketing.ResaleOpenedAt is null ? " (resale paused)" : string.Empty) + ", " +
            $"tickets for others {OnOff(save.Switches.PartnerTickets)}, donations {OnOff(save.Switches.Donations)}, " +
            $"mailing list opt-in {OnOff(save.Switches.MailingListOptIn)}, companion tickets {OnOff(save.Switches.CompanionTicketsOn)}", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> SetResaleOpenAsync(
        Guid settingsId, bool open, Guid actorUserId, CancellationToken ct = default)
    {
        var ticketing = await repository.GetTicketingSettingsAsync(settingsId, ct);
        if (ticketing is null)
            return new("Those ticketing settings no longer exist.");
        if (ticketing.CancelledAt is not null)
            return new("The event is cancelled.");
        if (open && ticketing.EffectiveSwitches.Resale == ResaleScheme.Off)
            return new("Resale is switched off in the ticketing settings.");
        if (!open && ticketing.Mode == TicketSalesMode.ResaleOnly)
            return new("Resale only mode always sells resale; close sales to stop it.");
        if (ticketing.ResaleOpenedAt is not null == open)
            return CheckoutMutationResult.Ok;

        var now = clock.GetCurrentInstant();
        ticketing.ResaleOpenedAt = open ? now : null;
        ticketing.UpdatedAt = now;
        await repository.SaveTicketingSettingsAsync(ticketing, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleOpenSet, TicketingSettingsAuditEntityType, ticketing.Id,
            open ? "Resale opened by hand." : "Resale paused by hand.", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<IReadOnlyList<BuyPage>> GetBuyPagesAsync(Guid userId, CancellationToken ct = default)
    {
        var pages = new List<BuyPage>();
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
        {
            if (ticketing.Mode == TicketSalesMode.Closed)
                continue;
            if (await LoadBuyContextAsync(ticketing.Id, userId, ct) is not { } context)
                continue;
            if (ticketing.Mode == TicketSalesMode.InvitedOnly && !context.Invited)
                continue;
            pages.Add(ToBuyPage(context, await RulesForAsync(ticketing, ct), null, directed: false));
        }
        return pages;
    }

    public async Task<IReadOnlyList<PublicSale>> GetPublicSalesAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var sales = new List<PublicSale>();
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
        {
            if (ticketing.IsClosed(now) || ticketing.Mode == TicketSalesMode.InvitedOnly
                || PlatformFor(ticketing.PlatformKey) is not { IsConfigured: true }
                || await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct) is not { } evt
                || TicketPrice.Public(ticketing, now.InZone(evt.TimeZone).Date) is not { } price)
                continue;
            sales.Add(new PublicSale(evt.EventName, price.AmountMinor, ticketing.Mode == TicketSalesMode.ResaleOnly)
            {
                GateOpeningDate = evt.GateOpeningDate,
            });
        }
        return sales;
    }

    /// <summary>
    /// The linked ticket while it can sell; once someone else bought it (or its holder took it back) the same link buys
    /// the next ticket in the queue, and the page says so.
    /// </summary>
    public async Task<BuyPage?> GetDirectedBuyPageAsync(string token, Guid userId, CancellationToken ct = default)
    {
        var tokenHash = HashToken(token);
        var offer = await repository.GetOfferByTokenHashAsync(tokenHash, ct);
        if (offer is null || await LoadBuyContextAsync(offer.TicketingSettingsId, userId, ct) is not { } context
            || context.Ticketing.EffectiveSwitches.Resale != ResaleScheme.QueueAndDirected)
            return null;
        var rules = await RulesForAsync(context.Ticketing, ct);
        if (InviteRefusal(offer, tokenHash, context) is { } refusal)
            return ToBuyPage(context, rules, refusal, directed: true);
        if (DirectedOfferFor(offer, tokenHash, context) is not null)
            return ToBuyPage(context, rules, null, directed: true);
        var stock = await LoadStockAsync(context.Ticketing, context.Now, ct);
        var queued = stock.ResaleOpen && stock.Offers.Any(o => o.State == TicketResaleOfferState.Open && o.HolderUserId != userId);
        return ToBuyPage(context, rules, null, directed: queued) with { InviteTaken = true };
    }

    private async Task<TicketRules?> RulesForAsync(TicketingSettings ticketing, CancellationToken ct) =>
        ticketing.TermsFolder is { } folder ? (await rulesSource.GetAsync(folder, ct: ct)).Rules : null;

    /// <summary>
    /// A ticket in resale sells at the resale price, so a page that can only sell one (directed, resale only, or fresh
    /// stock sold out) shows that price.
    /// </summary>
    private static BuyPage ToBuyPage(BuyContext context, TicketRules? rules, string? directedRefusal, bool directed) => new(
        context.Ticketing, context.Event.EventName,
        directed || context.Ticketing.Mode == TicketSalesMode.ResaleOnly || context.FreshSoldOut ? context.ResalePrice : context.OwnPrice,
        context.PublicPrice,
        directedRefusal ?? context.Unavailable ?? (context.Ticketing.TermsFolder is not null && rules is null ? "Tickets_Buy_TermsUnavailable" : null),
        context.AlreadyHoldsTicket, context.Buyer?.FullName ?? string.Empty,
        context.UserOrders.Where(o => o.TicketingSettingsId == context.Ticketing.Id).ToList())
    {
        GateOpeningDate = context.Event.GateOpeningDate,
        Terms = rules,
        AskMarketing = context.AskMarketing,
        OffersOthers = context.Ticketing.EffectiveSwitches.PartnerTickets && context.Ticketing.Mode != TicketSalesMode.InvitedOnly,
        OffersDonations = context.Ticketing.EffectiveSwitches.Donations,
        OpenOrder = context.UserOrders
            .Where(o => o.TicketingSettingsId == context.Ticketing.Id && o.BuyerUserId == context.Buyer?.Id
                && o.State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending && o.HoldsTickets(context.Now))
            .MaxBy(o => o.CreatedAt),
    };

    public async Task<StartCheckoutResult> StartCheckoutAsync(
        StartCheckout command, Guid userId, CancellationToken ct = default)
    {
        var context = await LoadBuyContextAsync(command.TicketingSettingsId, userId, ct);
        if (context is null)
            return StartCheckoutResult.Refused("Tickets_Buy_NotOnSale");
        if (context.Unavailable is { } unavailable)
            return StartCheckoutResult.Refused(unavailable);
        var buyer = context.Buyer!;
        var ticketing = context.Ticketing;
        var switches = ticketing.EffectiveSwitches;
        var now = context.Now;

        var otherEmails = command.OtherEmails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (otherEmails.Count > 0 && (!switches.PartnerTickets || ticketing.Mode == TicketSalesMode.InvitedOnly))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        if (otherEmails.Count > MaxOthers)
            return StartCheckoutResult.Refused("Tickets_Buy_OthersTooMany", MaxOthers.ToString(CultureInfo.InvariantCulture));
        if (!switches.Donations && (command.VipTotalMinor is not null || command.DonationMinor > 0))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        if (command.ForSelf && context.AlreadyHoldsTicket)
            return StartCheckoutResult.Refused("Tickets_Buy_AlreadyHolds");
        if (command.DonationMinor < 0)
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        var children = command.Children.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => (Name: c.Name!.Trim(), Type: ChildTypeFor(ticketing, c.Type))).ToList();
        if (children.Count > MaxChildren || children.Any(c => c.Name.Length > MaxNameLength || c.Type is null)
            || (children.Count > 0 && !command.ForSelf))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        string? donationPurpose = null;
        if (command.DonationMinor > 0 && !string.IsNullOrWhiteSpace(command.DonationPurpose))
        {
            donationPurpose = ticketing.DonationPurposes?.FirstOrDefault(p => string.Equals(p, command.DonationPurpose.Trim(), StringComparison.Ordinal));
            if (donationPurpose is null)
                return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        }
        if (command.VipTotalMinor is { } requested
            && (!command.ForSelf || !ticketing.VipTotalsMinor.Contains(requested)))
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        TicketRules? terms = null;
        if (ticketing.TermsFolder is { } termsFolder)
        {
            if (!command.AcceptTerms)
                return StartCheckoutResult.Refused("Tickets_Buy_TermsRequired");
            terms = (await rulesSource.GetAsync(termsFolder, ct: ct)).Rules;
            if (terms is null)
                return StartCheckoutResult.Refused("Tickets_Buy_TermsUnavailable");
            // The buyer accepts the text they were shown; rules changed since then are shown again first. The folder
            // counts too: two folders changed in one commit share a version.
            if (!string.Equals(terms.Folder, command.TermsFolder, StringComparison.Ordinal)
                || !string.Equals(terms.Version, command.TermsVersion, StringComparison.Ordinal))
                return StartCheckoutResult.Refused("Tickets_Buy_TermsChanged");
        }

        var others = new List<UserInfo>();
        foreach (var otherEmail in otherEmails)
        {
            var (other, problem) = await FindOtherAsync(otherEmail, buyer.Id, ticketing.Id, now, ct);
            if (problem is not null)
                return StartCheckoutResult.Refused("Tickets_Buy_Other" + problem, otherEmail);
            if (others.All(o => o.Id != other!.Id))
                others.Add(other!);
        }

        TicketResaleOffer? directed = null;
        if (command.DirectedToken is { } token)
        {
            var tokenHash = HashToken(token);
            var invited = switches.Resale == ResaleScheme.QueueAndDirected
                ? await repository.GetOfferByTokenHashAsync(tokenHash, ct)
                : null;
            if (InviteRefusal(invited, tokenHash, context) is { } refusal)
                return StartCheckoutResult.Refused(refusal);
            if (!command.ForSelf || others.Count > 0)
                return StartCheckoutResult.Refused("Tickets_Resale_InviteSelfOnly");
            // Gone to someone else first: the link buys the next ticket in the queue, at the resale price like the invitation.
            directed = DirectedOfferFor(invited, tokenHash, context);
            if (directed is not null && await IsAdmittedAsync(directed.TicketId, ct))
                directed = null;
        }

        var stock = await LoadStockAsync(ticketing, now, ct);
        var queue = stock.ResaleOpen
            ? new Queue<TicketResaleOffer>(stock.Offers
                .Where(o => o.State == TicketResaleOfferState.Open && o.HolderUserId != buyer.Id && o.Id != directed?.Id)
                .OrderBy(o => o.OfferedAt))
            : new Queue<TicketResaleOffer>();
        // A ticket in resale sells only at the resale price (current public wave, discounts kept), so a buyer with a class
        // price or a pinned wave is served from fresh stock while there is any.
        var takesQueue = ticketing.Mode == TicketSalesMode.ResaleOnly || !IsDiscounted(context)
            || stock.Held + 1 > ticketing.Capacity || command.DirectedToken is not null;
        var resaleForSelf = command.ForSelf ? directed ?? (takesQueue && queue.Count > 0 ? queue.Dequeue() : null) : null;
        var selfPrice = resaleForSelf is null ? context.OwnPrice! : context.ResalePrice!;
        if (command.VipTotalMinor is { } total && total <= selfPrice.AmountMinor)
            return StartCheckoutResult.Refused("Tickets_Buy_Invalid");
        var resaleForOthers = others.Select(_ => queue.Count > 0 ? queue.Dequeue() : null).ToList();
        var fresh = (command.ForSelf && resaleForSelf is null ? 1 : 0) + resaleForOthers.Count(o => o is null) + children.Count;
        if (fresh > 0 && ticketing.Mode is not (TicketSalesMode.Open or TicketSalesMode.InvitedOnly))
            return StartCheckoutResult.Refused("Tickets_Buy_ResaleOnly");
        if (stock.Held + fresh > ticketing.Capacity)
            return StartCheckoutResult.Refused("Tickets_Buy_SoldOut");

        var lines = new List<TicketCheckoutOrderLine>();
        if (command.ForSelf)
            lines.Add(TicketLine(ticketing, selfPrice, buyer.FullName, buyer.Email!, buyer.Id, resaleForSelf?.Id));
        foreach (var child in children)
        {
            var line = ChildLine(ticketing, child.Type!, child.Name);
            line.ParentLineIndex = 0;
            lines.Add(line);
        }

        for (var i = 0; i < others.Count; i++)
            lines.Add(TicketLine(ticketing, context.PublicPrice!, others[i].FullName, others[i].Email!, others[i].Id,
                resaleForOthers[i]?.Id));

        if (command.VipTotalMinor is { } vipTotal)
            lines.Add(DonationLine(vipTotal - selfPrice.AmountMinor, null));
        if (command.DonationMinor > 0)
            lines.Add(DonationLine(command.DonationMinor, donationPurpose));
        if (lines.Count == 0)
            return StartCheckoutResult.Refused("Tickets_Buy_Empty");

        if (command.MarketingOptIn && context.AskMarketing)
            await preferences.UpdatePreferenceAsync(buyer.Id, MessageCategory.Marketing, optedOut: false, JobName, ct);

        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            BuyerUserId = buyer.Id,
            BuyerName = buyer.FullName,
            BuyerEmail = buyer.Email!,
            PlatformKey = ticketing.PlatformKey,
            State = TicketCheckoutOrderState.Created,
            Culture = command.Culture,
            TermsFolder = terms?.Folder,
            TermsVersion = terms?.Version,
            TotalMinor = lines.Sum(l => l.AmountMinor),
            CreatedAt = now,
            ExpiresAt = now + CheckoutLifetime,
            UpdatedAt = now,
            Lines = lines,
        };
        await repository.AddOrderAsync(order, ct);
        var ticketCount = lines.Count(l => l.Kind == OrderLineKind.Ticket);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.Created,
            $"{ticketCount} ticket(s), {FormatMinor(order.TotalMinor)} {order.Currency} on {order.PlatformKey}" +
            (lines.Any(l => l.ResaleOfferId is not null) ? ", from the resale queue" : string.Empty), buyer.Id, ct);
        if (terms is not null)
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.TermsAccepted,
                $"Accepted the ticket rules in {terms.Folder} at {terms.Version}: {rulesSource.Permalink(terms.Folder, terms.Version)}",
                buyer.Id, ct);
        foreach (var offer in resaleForOthers.Prepend(resaleForSelf).OfType<TicketResaleOffer>())
            await ReserveOfferAsync(offer, order.Id, ct);

        if (order.TotalMinor == 0)
        {
            // Nothing to charge (a free companion, discounts covering the price): no platform takes a zero order.
            MarkPaid(order, null, null, now);
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.StateChanged, "Paid: nothing to charge", null, ct);
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutPaid, AuditEntityType, order.Id,
                $"Free order issued {ticketCount} ticket(s) without a payment", buyer.Id);
            await CompletePaidAsync(order, ct);
            return new StartCheckoutResult(order.Id, null, null);
        }

        var platform = PlatformFor(order.PlatformKey)!;
        try
        {
            var session = await platform.CreateOrderAsync(new PlatformOrderRequest(
                order.Id,
                ticketing.EventSettingsId,
                ticketing.PlatformEventRef,
                order.BuyerName,
                order.BuyerEmail,
                order.Currency,
                command.Culture,
                order.Lines.Select(l => new PlatformOrderLine(l.Kind, PaymentPageLabel(l), l.AmountMinor, l.VatRate, l.HolderName)).ToList(),
                order.ExpiresAt,
                new Uri(command.ReturnUrlBase, order.Id.ToString())), ct);

            order.PlatformOrderId = session.PlatformOrderId;
            order.PaymentUrl = session.PaymentUrl.ToString();
            order.AdminUrl = session.AdminUrl?.ToString();
            order.State = TicketCheckoutOrderState.Pending;
            order.UpdatedAt = clock.GetCurrentInstant();
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CheckoutOpened,
                $"{order.PlatformKey} opened platform order {session.PlatformOrderId}", null, ct);
            return new StartCheckoutResult(order.Id, session.PaymentUrl, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ticketing platform {Platform} refused order {OrderId}", order.PlatformKey, order.Id);
            order.State = TicketCheckoutOrderState.Failed;
            order.UpdatedAt = clock.GetCurrentInstant();
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.PlatformRefused, Truncate(ex.Message), null, ct);
            await ReleaseOffersAsync(order, ct);
            return StartCheckoutResult.Refused("Tickets_Buy_PlatformFailed");
        }
    }

    public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetOrdersForSettingsAsync(settingsId, ct);

    public async Task<OrderDetail?> GetOrderDetailAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null)
            return null;
        var refunds = await repository.GetRefundsAsync(orderId, ct);
        var timeline = (await repository.GetOrderEventsAsync(orderId, ct)).OrderBy(e => e.At).ToList();
        return new OrderDetail(order, refunds, timeline, await repository.GetTicketsForOrderAsync(orderId, ct));
    }

    public async Task<BuyOrder?> GetBuyOrderAsync(Guid orderId, Guid viewerId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null)
            return null;
        var isBuyer = order.BuyerUserId == viewerId;
        if (!isBuyer && !order.Lines.Any(line => line.HolderUserId == viewerId))
            return null;

        // Only the order's own people may trigger a platform refresh.
        await RefreshAsync(order, ct);
        var tickets = await repository.GetTicketsForOrderAsync(orderId, ct);
        return new BuyOrder(order.Id, order.Reference, order.State, order.IsOpen, isBuyer,
            isBuyer ? order.TotalMinor : 0, isBuyer ? order.TicketTotalMinor : 0, isBuyer ? order.RefundedMinor : 0,
            isBuyer ? order.PaymentUrl : null,
            order.Lines.Where(line => isBuyer || line.HolderUserId == viewerId).ToList(),
            tickets.Where(ticket => isBuyer || ticket.HolderUserId == viewerId).ToList());
    }

    public async Task PlatformOrderChangedAsync(string platformKey, string platformOrderId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderByPlatformIdAsync(platformKey, platformOrderId, ct);
        if (order is null)
        {
            // A webhook for an order Humans never opened (another environment sharing the account): ignore it.
            logger.LogInformation("Order change for unknown {Platform} order {PlatformOrderId}", platformKey, platformOrderId);
            return;
        }
        await RecoverAsync(order, ct);
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
                if (await RecoverAsync(order, ct))
                    changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconcile of order {OrderId} failed; next run retries", order.Id);
            }
        }
        foreach (var orderId in await UnsettledRefundOrdersAsync(ct))
        {
            try
            {
                if (await repository.GetOrderAsync(orderId, ct) is { } paid && await RecoverAsync(paid, ct))
                    changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Refund settlement of order {OrderId} failed; next run retries", orderId);
            }
        }
        changed += await RepairPaidAsync(ct);
        changed += await SweepOffersAsync(ct);
        changed += await SweepCancelledEventsAsync(ct);
        return changed;
    }

    private async Task<IReadOnlyList<Guid>> UnsettledRefundOrdersAsync(CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
            ids.UnionWith((await repository.GetRefundsForSettingsAsync(ticketing.Id, ct))
                .Where(r => r.State is RefundState.Pending or RefundState.ManualRequired)
                .Select(r => r.OrderId));
        return ids.ToList();
    }

    [ExternalWrite]
    private async Task<bool> RecoverAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        if (order.PlatformOrderId is null || PlatformFor(order.PlatformKey) is not { } platform)
            return await RefreshAsync(order, ct);

        var status = await platform.GetStatusAsync(order.PlatformOrderId, ct);
        var close = order.IsOpen && (status.State is PlatformOrderState.Expired or PlatformOrderState.Failed
            || (status.State == PlatformOrderState.Pending && order.ExpiresAt <= clock.GetCurrentInstant()));
        var closed = false;
        if (close)
        {
            ct.ThrowIfCancellationRequested();
            // Once cancellation reaches the platform, finish recording its outcome even during job shutdown.
            ct = CancellationToken.None;
            closed = await TryCancelAtPlatformAsync(platform, order, ct);
            if (closed && status.State == PlatformOrderState.Pending)
                status = status with { State = PlatformOrderState.Expired };
        }

        var changed = await ApplyStatusAsync(order, status, closed, ct);
        var settled = order.State == TicketCheckoutOrderState.Paid && await SettleRefundsAsync(order, status, ct);
        return changed || settled;
    }

    private async Task<bool> RefreshAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (order.PlatformOrderId is null)
        {
            // The platform never accepted it; once its hold lapses it is plainly expired.
            if (order.State != TicketCheckoutOrderState.Created || order.ExpiresAt > now)
                return false;
            order.State = TicketCheckoutOrderState.Expired;
            order.UpdatedAt = now;
            await repository.UpdateOrderAsync(order, ct);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.StateChanged, "Expired: never reached the platform", null, ct);
            await ReleaseOffersAsync(order, ct);
            return true;
        }

        var platform = PlatformFor(order.PlatformKey);
        if (platform is null)
        {
            logger.LogWarning("Order {OrderId} names unregistered platform {Platform}", order.Id, order.PlatformKey);
            return false;
        }

        var status = await platform.GetStatusAsync(order.PlatformOrderId, ct);
        return await ApplyStatusAsync(order, status, closureConfirmed: false, ct);
    }

    private async Task<bool> ApplyStatusAsync(
        TicketCheckoutOrder order, PlatformOrderStatus status, bool closureConfirmed, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var before = (order.State, order.RefundedMinor, order.FeeMinor, order.ChargedBackMinor);
        var newState = status.State switch
        {
            PlatformOrderState.Paid => TicketCheckoutOrderState.Paid,
            PlatformOrderState.Failed => TicketCheckoutOrderState.Failed,
            PlatformOrderState.Expired => TicketCheckoutOrderState.Expired,
            PlatformOrderState.Cancelled => TicketCheckoutOrderState.Cancelled,
            PlatformOrderState.Processing => TicketCheckoutOrderState.Processing,
            _ => TicketCheckoutOrderState.Pending,
        };

        // A paid order never goes back: a later platform state is a refund or a chargeback, read as RefundedMinor or ChargedBackMinor.
        if (order.State == TicketCheckoutOrderState.Paid)
            newState = TicketCheckoutOrderState.Paid;

        var wasOpen = order.IsOpen;
        // An expired/failed platform order may still take money until recovery confirms its cancellation.
        if (wasOpen && !closureConfirmed && newState is TicketCheckoutOrderState.Expired or TicketCheckoutOrderState.Failed)
            newState = order.State;

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
        order.ChargedBackMinor = status.ChargedBackMinor;

        if (order.State == TicketCheckoutOrderState.Processing && now - order.CreatedAt >= ProcessingAttention
            && !(await repository.GetOrderEventsAsync(order.Id, ct)).Any(e => e.Detail.StartsWith(ProcessingStuck, StringComparison.Ordinal)))
            await NeedsAttentionAsync(order.Id,
                $"{ProcessingStuck} for {ProcessingAttention.Days} days; it holds its tickets until {order.PlatformKey} settles it", ct);
        if (before == (order.State, order.RefundedMinor, order.FeeMinor, order.ChargedBackMinor))
            return false;

        order.UpdatedAt = now;
        await repository.UpdateOrderAsync(order, ct);

        if (before.State != order.State)
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.StateChanged,
                $"{order.PlatformKey} reports {order.State}" +
                (order.PaymentMethod is { } paidBy && order.State == TicketCheckoutOrderState.Paid ? $" by {paidBy}" : string.Empty) +
                (order.FeeMinor is { } fee ? $", fee {FormatMinor(fee)}" : string.Empty) +
                (late ? $", after the order was {before.State}: the ticketing team should check capacity" : string.Empty), null, ct);
        if (before.RefundedMinor != order.RefundedMinor)
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundedTotalChanged,
                $"{order.PlatformKey} reports {FormatMinor(order.RefundedMinor)} {order.Currency} refunded in total", null, ct);
        if (before.ChargedBackMinor != order.ChargedBackMinor)
        {
            var detail = order.ChargedBackMinor > 0
                ? $"{order.PlatformKey} reports a chargeback: {FormatMinor(order.ChargedBackMinor)} {order.Currency} taken back by the buyer's bank; the ticketing team should decide on the tickets"
                : $"{order.PlatformKey} reports the chargeback closed in the event's favour";
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundedTotalChanged, detail, null, ct);
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, order.Id, detail, JobName);
        }

        if (late)
            logger.LogWarning("Order {OrderId} was paid after it was {State}; its tickets are issued anyway",
                order.Id, before.State);
        if (becamePaid)
            await CompletePaidAsync(order, ct);
        else if (before.State != order.State && !order.IsOpen)
            await ReleaseOffersAsync(order, ct);
        if (wasOpen && order.State is TicketCheckoutOrderState.Failed or TicketCheckoutOrderState.Expired)
            await TellDeliveryAsync(d => d.PaymentEndedAsync(order.Id, ct), order.Id);

        if (before.State != order.State && !order.IsOpen)
        {
            var action = order.State == TicketCheckoutOrderState.Paid
                ? TicketsAuditActions.TicketCheckoutPaid
                : TicketsAuditActions.TicketCheckoutClosed;
            await auditLog.LogAsync(action, AuditEntityType, order.Id,
                $"{order.PlatformKey} reports the order {order.State}: {FormatMinor(order.TotalMinor)} {order.Currency}" +
                (order.PaymentMethod is { } method ? $" by {method}" : string.Empty) +
                (late ? $" after it was {before.State}" : string.Empty), JobName);
        }
        return true;
    }

    /// <summary>False when the platform refused, which means it was paid meanwhile: the next read records that.</summary>
    private async Task<bool> TryCancelAtPlatformAsync(ITicketingPlatform platform, TicketCheckoutOrder order, CancellationToken ct)
    {
        try
        {
            await platform.CancelAsync(order.PlatformOrderId!, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Closing order {OrderId} at {Platform} failed", order.Id, order.PlatformKey);
            return false;
        }
    }

    /// <summary>
    /// Settles open refunds, oldest first. A pending refund waits for its own answer: the state the platform gives for its
    /// id, or the resend in <c>RepairPaidAsync</c> when the call that made it never got one, so a refund made in the
    /// platform's dashboard never settles one of Tickets' own. Only manual refunds settle from what the refunded total
    /// does not already explain.
    /// </summary>
    private async Task<bool> SettleRefundsAsync(TicketCheckoutOrder order, PlatformOrderStatus status, CancellationToken ct)
    {
        var refunds = (await repository.GetRefundsAsync(order.Id, ct)).OrderBy(r => r.CreatedAt).ToList();
        var covered = status.RefundedMinor - refunds.Where(r => r.State == RefundState.Succeeded).Sum(r => r.AmountMinor);
        var changed = false;
        foreach (var refund in refunds.Where(r => r.State is RefundState.Pending or RefundState.ManualRequired))
        {
            ct.ThrowIfCancellationRequested();
            // A replay may still mutate the platform; finish this refund before honoring shutdown on the next one.
            var own = refund is { State: RefundState.Pending, PlatformRefundId: not null }
                && order.PlatformOrderId is not null && PlatformFor(order.PlatformKey) is { } platform
                ? await OwnRefundStateAsync(platform, order, refund, CancellationToken.None)
                : null;
            if (own is { State: RefundState.Succeeded }
                || (refund.State == RefundState.ManualRequired && refund.AmountMinor <= covered))
            {
                covered -= refund.AmountMinor;
                refund.State = RefundState.Succeeded;
            }
            else if (own is { State: RefundState.Failed })
            {
                refund.State = RefundState.Failed;
                refund.FailureReason = Truncate(own.FailureReason ?? $"{order.PlatformKey} reports the refund failed");
            }
            else
            {
                continue;
            }
            await repository.UpdateRefundAsync(refund, CancellationToken.None);
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundSettled,
                $"Refund of {FormatMinor(refund.AmountMinor)} {order.Currency} {refund.State} per {order.PlatformKey}", null, CancellationToken.None);
            changed = true;
        }
        return changed;
    }

    private async Task<RefundResult?> OwnRefundStateAsync(
        ITicketingPlatform platform, TicketCheckoutOrder order, TicketCheckoutRefund refund, CancellationToken ct)
    {
        try
        {
            return await platform.RefundAsync(new RefundRequest(refund.Id, order.PlatformOrderId!, refund.AmountMinor, refund.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "State of refund {RefundId} could not be read from {Platform}", refund.Id, order.PlatformKey);
            return null;
        }
    }

    public async Task<CheckoutMutationResult> RefundAsync(
        Guid orderId, long amountMinor, string reason, Guid actorUserId, CancellationToken ct = default) =>
        (await RefundCoreAsync(orderId, amountMinor, reason, actorUserId, ct)).Result;

    /// <summary>
    /// <paramref name="actorUserId"/> is null for automation: the resale refund owed when a ticket in resale sells, which
    /// passes the offer id as <paramref name="refundId"/> so a repeat finds the same refund instead of a second one.
    /// </summary>
    private async Task<(CheckoutMutationResult Result, Guid? RefundId)> RefundCoreAsync(
        Guid orderId, long amountMinor, string reason, Guid? actorUserId, CancellationToken ct, Guid? refundId = null)
    {
        if (refundId is { } existingId
            && (await repository.GetRefundsAsync(orderId, ct)).FirstOrDefault(r => r.Id == existingId) is { } existing)
        {
            if (existing is { State: RefundState.Pending, PlatformRefundId: null }
                && await repository.GetOrderAsync(orderId, ct) is { PlatformOrderId: not null } paidOrder
                && PlatformFor(paidOrder.PlatformKey) is { } retryPlatform)
                await CallPlatformRefundAsync(retryPlatform, paidOrder, existing, actorUserId, ct, resend: true);
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
        if (order.State != TicketCheckoutOrderState.Paid || order.PlatformOrderId is null)
            return (new("Only a paid order can be refunded."), null);
        // The guard Peter set for API refunds: tied to this payment, never above what its tickets cost.
        if (amountMinor > detail.RefundableMinor)
            return (new($"At most {FormatMinor(detail.RefundableMinor)} {order.Currency} of this order's tickets can still be refunded" +
                (order.ChargedBackMinor > 0 ? $"; the buyer's bank took {FormatMinor(order.ChargedBackMinor)} back in a chargeback." : ".")), null);
        var platform = PlatformFor(order.PlatformKey);
        if (platform is null)
            return (new($"Ticketing platform '{order.PlatformKey}' is not registered."), null);

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
        // Saved first, so a crash after the platform call still counts against what can be refunded.
        await repository.AddRefundAsync(refund, ct);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundRequested,
            $"Refund of {FormatMinor(amountMinor)} {order.Currency} requested: {Truncate(refund.Reason)}", actorUserId, ct);

        var result = await CallPlatformRefundAsync(platform, order, refund, actorUserId, ct);
        return (result.State == RefundState.Failed
            ? new($"The ticketing platform declined the refund: {result.FailureReason}")
            : CheckoutMutationResult.Ok, refund.Id);
    }

    /// <summary>A <paramref name="resend"/> that changes nothing is only logged, so a platform that stays down adds no rows.</summary>
    private async Task<RefundResult> CallPlatformRefundAsync(
        ITicketingPlatform platform, TicketCheckoutOrder order, TicketCheckoutRefund refund, Guid? actorUserId,
        CancellationToken ct, bool resend = false)
    {
        ct.ThrowIfCancellationRequested();
        // Finish the vendor attempt, its saved result and order refresh once a refund starts.
        ct = CancellationToken.None;
        var amountMinor = refund.AmountMinor;
        RefundResult result;
        try
        {
            result = await platform.RefundAsync(
                new RefundRequest(refund.Id, order.PlatformOrderId!, amountMinor, refund.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A timeout leaves the outcome unknown and the money may have gone: the status read settles it.
            logger.LogError(ex, "Refund {RefundId} of order {OrderId} threw in {Platform}", refund.Id, order.Id, order.PlatformKey);
            result = new RefundResult(RefundState.Pending, null, ex.Message);
        }

        if (resend && result.State == refund.State && string.Equals(result.PlatformRefundId, refund.PlatformRefundId, StringComparison.Ordinal))
        {
            logger.LogInformation("Resent refund {RefundId} of order {OrderId} is still {State}", refund.Id, order.Id, result.State);
            return result;
        }

        refund.State = result.State;
        refund.PlatformRefundId = result.PlatformRefundId;
        refund.FailureReason = result.FailureReason;
        refund.ManualRefundUrl = result.ManualRefundUrl?.ToString();
        await repository.UpdateRefundAsync(refund, ct);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.RefundSettled,
            $"{order.PlatformKey} answered {result.State}" + (result.PlatformRefundId is { } refundRef ? $" ({refundRef})" : string.Empty) +
            (result.FailureReason is { } failure ? $": {Truncate(failure)}" : string.Empty), null, ct);

        var description = $"Refund of {FormatMinor(amountMinor)} {order.Currency} through {order.PlatformKey}: {result.State}" +
            (result.FailureReason is { } why ? $" ({why})" : string.Empty) + $". Reason: {refund.Reason}";
        if (actorUserId is { } actor)
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutRefunded, AuditEntityType, order.Id, description, actor);
        else
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutRefunded, AuditEntityType, order.Id, description, JobName);

        if (result.State is RefundState.Succeeded or RefundState.Pending)
            await RecoverAsync(order, ct);
        return result;
    }

    public async Task<CheckoutMutationResult> CancelAsync(Guid orderId, Guid userId, CancellationToken ct = default)
    {
        var order = await repository.GetOrderAsync(orderId, ct);
        if (order is null || order.BuyerUserId != userId)
            return new("Tickets_Buy_OrderNotFound");
        if (order.State == TicketCheckoutOrderState.Processing)
            return new("Tickets_Buy_PaymentProcessing");
        if (!order.IsOpen)
            return CheckoutMutationResult.Ok;

        if (order.PlatformOrderId is not null && PlatformFor(order.PlatformKey) is { } platform)
        {
            // Paid in the meantime: the refresh records it instead of cancelling.
            await RecoverAsync(order, ct);
            if (order.State == TicketCheckoutOrderState.Paid)
                return new("Tickets_Buy_AlreadyPaid");
            if (order.State == TicketCheckoutOrderState.Processing)
                return new("Tickets_Buy_PaymentProcessing");
            await platform.CancelAsync(order.PlatformOrderId, ct);
        }

        order.State = TicketCheckoutOrderState.Cancelled;
        order.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateOrderAsync(order, ct);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.Cancelled, "Abandoned by the buyer", userId, ct);
        await ReleaseOffersAsync(order, ct);
        return CheckoutMutationResult.Ok;
    }


    /// <summary>A SEPA debit settles within about two weeks; one still on its way after that needs a person.</summary>
    private static readonly Duration ProcessingAttention = Duration.FromDays(14);

    private const string ProcessingStuck = "Payment on its way";

    internal const string OrdersSlice = "TicketCheckoutOrders";
    internal const string TicketsSlice = "TicketCheckoutTickets";
    internal const string OffersSlice = "TicketResaleOffers";
    internal const string CompanionRequestsSlice = "TicketCompanionRequests";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var orders = await repository.GetOrdersForUserAsync(userId, ct);
        var held = await repository.GetTicketsForHolderAsync(userId, ct);
        var tickets = held.Concat(await ChildrenOfAsync(held, ct)).ToList();
        var offers = new List<TicketResaleOffer>();
        foreach (var settingsId in tickets.Select(t => t.TicketingSettingsId).Distinct())
            offers.AddRange((await repository.GetOffersForSettingsAsync(settingsId, ct)).Where(o => o.HolderUserId == userId));
        var companionRequests = await repository.GetCompanionRequestsForUserAsync(userId, ct);
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
                TicketRulesAccepted = o.BuyerUserId == userId && o.TermsVersion is not null ? $"{o.TermsFolder} at {o.TermsVersion}" : null,
                Lines = o.Lines
                    .Where(l => o.BuyerUserId == userId || l.HolderUserId == userId
                        || (l.ParentLineIndex is { } parent && o.Lines[parent].HolderUserId == userId))
                    .Select(l => new { Kind = l.Kind.ToString(), l.Description, Amount = FormatMinor(l.AmountMinor), l.HolderName, l.HolderEmail })
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
                InvitedEmails = o.Invites.Select(i => i.Email).ToList(),
                UnsoldNotifiedAt = o.UnsoldNotifiedAt.ToIso8601(),
            }).ToList()),
            new UserDataSlice(CompanionRequestsSlice, companionRequests.Select(r => new
            {
                r.Id,
                r.TicketId,
                AskedForCompanion = r.HolderUserId == userId,
                // The holder's reason is about the holder; the companion sees only that they were asked for.
                Note = r.HolderUserId == userId ? r.Note : null,
                State = r.State.ToString(),
                RequestedAt = r.RequestedAt.ToIso8601(),
                DecidedAt = r.DecidedAt.ToIso8601(),
                DecisionNote = r.HolderUserId == userId ? r.DecisionNote : null,
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
                "Partly retained: the invited buyers' emails and their links are cleared; dates, state and the refund " +
                "owed stay as the accounting record of the resale.",
            [CompanionRequestsSlice] =
                "Partly retained: the holder's note is replaced by the tombstone and the team's answer cleared; state and dates stay as the record of " +
                "why a free ticket was issued.",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The platforms' copies go first: once Tickets' own copy is tombstoned the emails to erase are gone, so a platform
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
            var people = order.Lines.Where(l => l.HolderUserId == userId).Select(l => l.HolderEmail)
                .Append(order.BuyerUserId == userId ? order.BuyerEmail : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            addresses.UnionWith(people);
            if (order.PlatformOrderId is null)
                continue;
            if (PlatformFor(order.PlatformKey) is not { } platform)
            {
                // A retired platform's data must be erased or exported before its adapter is removed; this only
                // keeps one leftover order from blocking every later deletion.
                logger.LogWarning("Order {OrderId} is on retired platform {Platform}; its copy there was not erased", order.Id, order.PlatformKey);
                await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, order.Id,
                    $"GDPR erasure could not reach retired platform {order.PlatformKey}", JobName);
                continue;
            }
            foreach (var personEmail in people)
                await platform.ErasePersonalDataAsync(order.PlatformOrderId, personEmail, ct);
        }

        var now = clock.GetCurrentInstant();
        var held = await repository.GetTicketsForHolderAsync(userId, ct);
        foreach (var ticket in held.Concat(await ChildrenOfAsync(held, ct))
                     .Where(t => t.IsHeld))
        {
            ticket.State = TicketCheckoutTicketState.Void;
            ticket.VoidedAt = now;
            ticket.VoidReason = "Holder's personal data erased";
            await repository.UpdateTicketAsync(ticket, ct);
        }
        foreach (var settingsId in held.Select(t => t.TicketingSettingsId).Distinct())
            foreach (var offer in (await repository.GetOffersForSettingsAsync(settingsId, ct))
                         .Where(o => o.HolderUserId == userId && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved))
            {
                offer.State = TicketResaleOfferState.Withdrawn;
                offer.WithdrawnAt = now;
                await repository.UpdateOfferAsync(offer, ct);
            }
        foreach (var request in (await repository.GetCompanionRequestsForUserAsync(userId, ct))
                     .Where(r => r.State == TicketCompanionRequestState.Pending))
        {
            request.State = TicketCompanionRequestState.Withdrawn;
            request.DecidedAt = now;
            await repository.UpdateCompanionRequestAsync(request, ct);
        }
        await repository.ErasePiiForUserAsync(userId, addresses, ct);
    }

    private async Task<IReadOnlyList<TicketCheckoutTicket>> ChildrenOfAsync(
        IReadOnlyList<TicketCheckoutTicket> adults, CancellationToken ct)
    {
        var ids = adults.Select(t => t.Id).ToHashSet();
        var children = new List<TicketCheckoutTicket>();
        foreach (var settingsId in adults.Select(t => t.TicketingSettingsId).Distinct())
            children.AddRange((await repository.GetTicketsForSettingsAsync(settingsId, ct))
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

    private static string Truncate(string text, int max = 500)
    {
        if (text.Length <= max) return text;
        var length = max;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length];
    }

    private void MarkPaid(TicketCheckoutOrder order, long? feeMinor, string? method, Instant paidAt)
    {
        order.State = TicketCheckoutOrderState.Paid;
        order.PaidAt = paidAt;
        order.FeeMinor = feeMinor;
        order.PaymentMethod = method;
        logger.LogInformation("Order {OrderId} paid", order.Id);
    }

    private sealed record BuyContext(
        TicketingSettings Ticketing,
        EventSettingsInfo Event,
        UserInfo? Buyer,
        TicketPrice? OwnPrice,
        TicketPrice? PublicPrice,
        TicketPrice? ResalePrice,
        string? Unavailable,
        bool AlreadyHoldsTicket,
        IReadOnlyList<TicketCheckoutOrder> UserOrders,
        bool AskMarketing,
        Instant Now,
        bool FreshSoldOut,
        bool Invited);

    private async Task<BuyContext?> LoadBuyContextAsync(Guid settingsId, Guid userId, CancellationToken ct)
    {
        if (await repository.GetTicketingSettingsAsync(settingsId, ct) is not { } ticketing)
            return null;
        if (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct) is not { } evt)
            return null;

        var now = clock.GetCurrentInstant();
        var zone = evt.TimeZone;
        var today = now.InZone(zone).Date;
        var buyer = await users.GetUserInfoAsync(userId, ct);
        var activeGrants = (await grants.GetActiveGrantsAsync(userId, ct))
            .Where(g => g.EventSettingsId is null || g.EventSettingsId == evt.Id)
            .ToList();
        var programIds = activeGrants.Select(g => g.ProgramId).ToHashSet();
        var pinPrograms = ticketing.PriceRules.Where(r => r.Kind == TicketPriceRuleKind.WavePin).Select(r => r.GrantProgramId).ToHashSet();
        var pinnedOn = activeGrants.Where(g => pinPrograms.Contains(g.ProgramId))
            .Select(g => (LocalDate?)g.IssuedAt.InZone(zone).Date)
            .Min();
        var invited = ticketing.PriceRules.Any(r => r.Kind == TicketPriceRuleKind.EarlyAccess && programIds.Contains(r.GrantProgramId));
        var early = ticketing.Mode == TicketSalesMode.InvitedOnly;
        var ownPrice = TicketPrice.For(ticketing, today, programIds, pinnedOn, early);
        var publicPrice = TicketPrice.Public(ticketing, today);
        var discountPrograms = ticketing.PriceRules.Where(r => r.Kind == TicketPriceRuleKind.Discount)
            .Select(r => r.GrantProgramId).Where(programIds.Contains).ToHashSet();
        var resalePrice = TicketPrice.For(ticketing, today, discountPrograms);
        var userOrders = await repository.GetOrdersForUserAsync(userId, ct);
        var holds = await HoldsTicketAsync(ticketing.Id, userId, now, ct);
        var askMarketing = ticketing.EffectiveSwitches.MailingListOptIn && await preferenceReads.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, ct) is null;

        string? unavailable = null;
        if (ticketing.IsClosed(now))
            unavailable = "Tickets_Buy_Closed";
        else if (ownPrice is null || publicPrice is null || (early && !invited))
            unavailable = "Tickets_Buy_NotOnSale";
        else if (PlatformFor(ticketing.PlatformKey) is not { IsConfigured: true })
            unavailable = "Tickets_Buy_NotOnSale";
        else if (buyer is null || !buyer.HasRequiredNameFields || string.IsNullOrWhiteSpace(buyer.Email))
            unavailable = "Tickets_Buy_NeedLegalName";

        var freshSoldOut = (await LoadStockAsync(ticketing, now, ct)).Held + 1 > ticketing.Capacity;
        return new BuyContext(ticketing, evt, buyer, ownPrice, publicPrice, resalePrice, unavailable, holds, userOrders,
            askMarketing, now, freshSoldOut, invited);
    }

    /// <summary>The line's name on the payment page, in the buyer's language (the request culture at checkout).</summary>
    private string PaymentPageLabel(TicketCheckoutOrderLine line) => line.Kind == OrderLineKind.Donation
        ? localizer["Tickets_Buy_Line_Donation", line.DonationPurpose ?? localizer["Tickets_Buy_Purpose_General"].Value]
        : line.HolderKind == TicketHolderKind.Child
            ? $"{localizer["Tickets_Buy_Line_Child"].Value} ({line.WaveName})"
            : $"{localizer["Tickets_Buy_Line_Ticket"].Value} ({line.ClassLabel ?? line.WaveName})";

    private static bool IsDiscounted(BuyContext context) =>
        context.OwnPrice!.ClassPrice is not null || !ReferenceEquals(context.OwnPrice.Wave, context.PublicPrice!.Wave);

    /// <summary>An adult ticket held, in resale, or on an unpaid order that has not lapsed.</summary>
    private async Task<bool> HoldsTicketAsync(Guid settingsId, Guid userId, Instant now, CancellationToken ct)
    {
        if ((await repository.GetTicketsForHolderAsync(userId, ct)).Any(t =>
                t.TicketingSettingsId == settingsId && t.Kind != TicketHolderKind.Child
                && t.IsHeld))
            return true;
        return (await repository.GetOrdersForUserAsync(userId, ct)).Any(o =>
            o.TicketingSettingsId == settingsId && o.State != TicketCheckoutOrderState.Paid && o.HoldsTickets(now)
            && o.Lines.Any(l => l.Kind == OrderLineKind.Ticket && l.HolderUserId == userId && l.HolderKind != TicketHolderKind.Child));
    }

    /// <summary>
    /// <paramref name="Held"/> counts against capacity: issued tickets still valid or in resale, and fresh-stock lines on
    /// unpaid orders that have not lapsed. A resold ticket replaces the one put up for resale, so it adds nothing.
    /// </summary>
    private sealed record Stock(int Held, bool ResaleOpen, IReadOnlyList<TicketResaleOffer> Offers);

    private async Task<Stock> LoadStockAsync(TicketingSettings ticketing, Instant now, CancellationToken ct)
    {
        var tickets = await repository.GetTicketsForSettingsAsync(ticketing.Id, ct);
        var sold = tickets.Count(t => t.IsHeld);
        var pending = (await repository.GetOrdersForSettingsAsync(ticketing.Id, ct))
            .Where(o => o.State != TicketCheckoutOrderState.Paid && o.HoldsTickets(now))
            .Sum(o => o.Lines.Count(l => l.Kind == OrderLineKind.Ticket && l.ResaleOfferId is null));
        var resaleOpen = ticketing.IsResaleSelling(now);
        // A ticket in resale whose holder was already admitted can never sell: the gate's admission is the only entry record.
        var offers = await repository.GetOffersForSettingsAsync(ticketing.Id, ct);
        var offeredCodes = tickets
            .Where(t => t.State == TicketCheckoutTicketState.Offered)
            .ToDictionary(t => t.Id, t => tickets
                .Where(c => c.Id == t.Id || (c.ParentTicketId == t.Id && c.State == TicketCheckoutTicketState.Valid))
                .Select(c => c.Barcode)
                .ToList());
        var admitted = await gateAdmissions.GetAdmittedAsync(offeredCodes.Values.SelectMany(c => c).ToList(), ct);
        return new Stock(sold + pending, resaleOpen,
            offers.Where(o => !(offeredCodes.TryGetValue(o.TicketId, out var codes) && codes.Any(admitted.Contains))).ToList());
    }

    private static TicketCheckoutOrderLine TicketLine(
        TicketingSettings ticketing, TicketPrice price, string name, string email, Guid? userId, Guid? resaleOfferId) => new()
        {
            Kind = OrderLineKind.Ticket,
            HolderKind = TicketHolderKind.Adult,
            Description = price.ClassPrice is { } cls ? $"Ticket: {cls.Label}" : $"Ticket: {price.Wave.Name}",
            AmountMinor = price.AmountMinor,
            VatRate = ticketing.TicketVatRate,
            HolderName = name,
            HolderEmail = email,
            HolderUserId = userId,
            WaveName = price.Wave.Name,
            ClassProgramId = price.ClassPrice?.GrantProgramId,
            ClassLabel = price.ClassPrice?.Label,
            DiscountsMinor = price.Discounts.Sum(c => c.AmountMinor),
            ResaleOfferId = resaleOfferId,
        };

    /// <summary>The named child type, or the event's only one when no name is given; null when there is no such type.</summary>
    private static TicketChildType? ChildTypeFor(TicketingSettings ticketing, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? ticketing.ChildTypes.Count == 1 ? ticketing.ChildTypes[0] : null
            : ticketing.ChildTypes.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.Ordinal));

    private static TicketCheckoutOrderLine ChildLine(TicketingSettings ticketing, TicketChildType type, string name) => new()
    {
        Kind = OrderLineKind.Ticket,
        HolderKind = TicketHolderKind.Child,
        Description = $"Child ticket: {type.Name}",
        AmountMinor = type.PriceMinor,
        VatRate = ticketing.TicketVatRate,
        HolderName = name,
        WaveName = type.Name,
    };

    private static TicketCheckoutOrderLine DonationLine(long amountMinor, string? purpose) => new()
    {
        Kind = OrderLineKind.Donation,
        Description = purpose is null ? "Donation" : $"Donation: {purpose}",
        AmountMinor = amountMinor,
        VatRate = 0m,
        DonationPurpose = purpose,
    };

    /// <summary>
    /// The person behind an address someone gives for another person (on an order, as a companion), or the problem:
    /// <c>NotFound</c>, <c>IsYou</c>, <c>NeedsName</c> or <c>Holds</c>, the suffix of the caller's refusal key. Only an
    /// address verified on exactly one live account counts; nobody gets an account made for them.
    /// </summary>
    private async Task<(UserInfo? Person, string? Problem)> FindOtherAsync(
        string address, Guid askerId, Guid settingsId, Instant now, CancellationToken ct)
    {
        if (!LooksLikeEmail(address) || address.Length > MaxEmailLength)
            return (null, "NotFound");
        var owners = (await userEmails.FindByAddressAsync(address, aliased: true, verifiedOnly: true, ct))
            .Select(r => r.UserId).Distinct().ToList();
        return owners.Count != 1 ? (null, "NotFound") : await CheckOtherAsync(owners[0], askerId, settingsId, now, ct);
    }

    private async Task<(UserInfo? Person, string? Problem)> CheckOtherAsync(
        Guid userId, Guid askerId, Guid settingsId, Instant now, CancellationToken ct)
    {
        if (await users.GetUserInfoAsync(userId, ct) is not { } person
            || person.IsTombstone || person.IsFake || person.State is UserState.DeletePending or UserState.Rejected)
            return (null, "NotFound");
        if (person.Id == askerId)
            return (null, "IsYou");
        if (!person.HasRequiredNameFields || string.IsNullOrWhiteSpace(person.Email))
            return (null, "NeedsName");
        if (await HoldsTicketAsync(settingsId, person.Id, now, ct))
            return (null, "Holds");
        return (person, null);
    }

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

    /// <summary>
    /// Directed resale switched off: a live link could no longer be used, so it is ended and the holder sees it gone.
    /// The offer was never out of the queue. An offer held by an unpaid order keeps its buyer.
    /// </summary>
    private async Task EndInvitationsAsync(Guid settingsId, Guid actorUserId, CancellationToken ct)
    {
        foreach (var offer in (await repository.GetOffersForSettingsAsync(settingsId, ct))
                     .Where(o => o.State == TicketResaleOfferState.Open && o.HasLiveInvite))
        {
            offer.Invites = [];
            await repository.UpdateOfferAsync(offer, ct);
            if (await repository.GetTicketAsync(offer.TicketId, ct) is { } ticket)
                await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInviteCancelled,
                    $"Invitations for ticket {ticket.Barcode} ended: directed resale switched off; it keeps its place in the queue",
                    actorUserId, ct);
        }
    }

    /// <summary>
    /// Resale switched off: a queued ticket would never sell, so each open offer is withdrawn and the ticket is the
    /// holder's again. An offer held by an unpaid order keeps its buyer; if that order fails it is withdrawn on release.
    /// </summary>
    private async Task WithdrawOpenOffersAsync(Guid settingsId, Guid actorUserId, CancellationToken ct)
    {
        foreach (var offer in (await repository.GetOffersForSettingsAsync(settingsId, ct))
                     .Where(o => o.State == TicketResaleOfferState.Open))
            await WithdrawSwitchedOffAsync(offer, actorUserId, ct);
    }

    private async Task WithdrawSwitchedOffAsync(TicketResaleOffer offer, Guid? actorUserId, CancellationToken ct)
    {
        offer.State = TicketResaleOfferState.Withdrawn;
        offer.WithdrawnAt = clock.GetCurrentInstant();
        offer.ReservedByOrderId = null;
        offer.Invites = [];
        await repository.UpdateOfferAsync(offer, ct);
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } ticket)
            return;
        ticket.State = TicketCheckoutTicketState.Valid;
        await repository.UpdateTicketAsync(ticket, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleWithdrawn,
            $"Ticket {ticket.Barcode} taken back out of the resale queue: resale switched off for this event", actorUserId, ct);
        const string detail = "withdrawn from resale: resale switched off for this event";
        if (actorUserId is { } actor)
            await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawn, AuditEntityType, ticket.OrderId,
                $"Ticket {ticket.Barcode} {detail}", actor);
        else
            await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawn, AuditEntityType, ticket.OrderId,
                $"Ticket {ticket.Barcode} {detail}", JobName);
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>Links a holder may send for one ticket over all its offers: enough for friends, too few to spam with.</summary>
    internal const int MaxInvitesPerTicket = 10;

    /// <summary>How long a resale buyer's payment must stand before the evening batch refunds the old holder.</summary>
    internal static readonly Duration ResaleRefundHold = Duration.FromHours(12);

    private const string RefundHeldBack = "Resale refund held back";
    private static readonly Duration DeliveryGrace = Duration.FromMinutes(30);
    private static readonly Duration DeliveryRepairWindow = Duration.FromDays(7);

    public Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default) =>
        repository.GetTicketAsync(id, ct);

    public Task RecordDeliveryAsync(
        Guid orderId, TicketCheckoutOrderEventKind kind, string detail, Guid? actorUserId, CancellationToken ct = default)
    {
        if (kind is not (TicketCheckoutOrderEventKind.TicketsEmailed or TicketCheckoutOrderEventKind.TicketsResent
            or TicketCheckoutOrderEventKind.TicketMailSkipped))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only delivery steps are recorded here.");
        return TimelineAsync(orderId, kind, Truncate(detail), actorUserId, ct);
    }

    public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default) =>
        repository.GetTicketsForHolderAsync(userId, ct);

    public async Task<int> GetHeldTicketCountAsync(Guid userId, CancellationToken ct = default)
    {
        var activeEvent = settings.GetActiveEventSettings();
        var forEvent = (await repository.ListTicketingSettingsAsync(ct))
            .Where(e => activeEvent is null || e.EventSettingsId == activeEvent.Id || e.Mode != TicketSalesMode.Closed)
            .Select(e => e.Id).ToHashSet();
        return (await repository.GetTicketsForHolderAsync(userId, ct))
            .Count(t => t.IsHeld && forEvent.Contains(t.TicketingSettingsId));
    }

    public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetTicketsForSettingsAsync(settingsId, ct);

    public Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetOffersForSettingsAsync(settingsId, ct);

    public Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetRefundsForSettingsAsync(settingsId, ct);

    public async Task<IReadOnlyList<HeldTicket>> GetHeldTicketsAsync(Guid userId, CancellationToken ct = default)
    {
        var held = new List<HeldTicket>();
        var now = clock.GetCurrentInstant();
        foreach (var group in (await repository.GetTicketsForHolderAsync(userId, ct))
                     .Where(t => t.IsHeld)
                     .GroupBy(t => t.TicketingSettingsId))
        {
            var ticketing = await repository.GetTicketingSettingsAsync(group.Key, ct);
            var eventName = ticketing is null ? string.Empty
                : (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? string.Empty;
            var offers = await repository.GetOffersForSettingsAsync(group.Key, ct);
            var queue = offers.Where(o => o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved).ToList();
            var positions = TicketSupportReports.QueuePositions(offers);
            var eventTickets = ticketing is null ? [] : await repository.GetTicketsForSettingsAsync(group.Key, ct);
            var companionRequests = await repository.GetCompanionRequestsForSettingsAsync(group.Key, ct);
            foreach (var ticket in group)
            {
                var offer = queue.FirstOrDefault(o => o.TicketId == ticket.Id);
                var hasDependents = eventTickets.Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid);
                held.Add(new HeldTicket(ticket, eventName, offer, offer is null ? null : positions[offer.Id],
                    CanOffer: ticketing is { Mode: not TicketSalesMode.InvitedOnly } && !ticketing.IsClosed(now)
                        && ticketing.EffectiveSwitches.Resale != ResaleScheme.Off && offer is null && ticket.Kind == TicketHolderKind.Adult,
                    HasDependents: hasDependents,
                    DirectedActive: offer?.HasLiveInvite == true)
                {
                    CanInvite = ticketing?.EffectiveSwitches.Resale == ResaleScheme.QueueAndDirected && ticketing.IsResaleSelling(now),
                    CompanionRequest = companionRequests.Where(r => r.TicketId == ticket.Id).MaxBy(r => r.RequestedAt),
                    CanAskCompanion = CanAskCompanion(ticket, ticketing, eventTickets, companionRequests),
                });
                // A child ticket has no account of its own: it shows under the adult who holds it.
                foreach (var child in eventTickets.Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
                    held.Add(new HeldTicket(child, eventName, null, null, CanOffer: false, HasDependents: false, DirectedActive: false));
            }
        }
        return held;
    }

    public async Task<CheckoutMutationResult> OfferForResaleAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != userId)
            return new("Tickets_Resale_NotFound");
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult)
            return new("Tickets_Resale_NotOfferable");
        var ticketing = await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct);
        var now = clock.GetCurrentInstant();
        if (ticketing is null || ticketing.IsClosed(now) || ticketing.Mode == TicketSalesMode.InvitedOnly
            || ticketing.EffectiveSwitches.Resale == ResaleScheme.Off)
            return new("Tickets_Resale_Closed");
        // The bank already took this order's money back: a resale would owe a refund of money Humans no longer holds.
        if (await repository.GetOrderAsync(ticket.OrderId, ct) is { ChargedBackMinor: > 0 })
            return new("Tickets_Resale_NotOfferable");

        // Children go wherever their adult goes: put up for resale with the adult and refunded with it when it sells.
        // Event-wide: an admin-issued companion lives on its own order.
        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        if (await IsAdmittedAsync(ticket, ct))
            return new("Tickets_Resale_NotOfferable");
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticket.TicketingSettingsId,
            TicketId = ticket.Id,
            HolderUserId = userId,
            OfferedAt = now,
            State = TicketResaleOfferState.Open,
            RefundOwedMinor = ticket.PricePaidMinor + dependents.Sum(d => d.PricePaidMinor),
        };
        await repository.AddOfferAsync(offer, ct);
        ticket.State = TicketCheckoutTicketState.Offered;
        await repository.UpdateTicketAsync(ticket, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleOffered,
            $"Ticket {ticket.Barcode} put up for resale; {FormatMinor(offer.RefundOwedMinor)} owed if it sells" +
            (dependents.Count > 0 ? $", with {dependents.Count} child ticket(s)" : string.Empty), userId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleOffered, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} offered for resale", userId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> WithdrawOfferAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        if (ticket is null || offer is null)
            return new("Tickets_Resale_NotFound");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("Tickets_Resale_InCheckout");

        offer.State = TicketResaleOfferState.Withdrawn;
        offer.WithdrawnAt = clock.GetCurrentInstant();
        await repository.UpdateOfferAsync(offer, ct);
        ticket.State = TicketCheckoutTicketState.Valid;
        await repository.UpdateTicketAsync(ticket, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleWithdrawn,
            $"Ticket {ticket.Barcode} taken back out of the resale queue", userId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawn, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} withdrawn from resale", userId);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>
    /// Emails the link to each new address. Every link sells this one ticket to whoever buys first; anyone later buys the
    /// next ticket in the queue. An address already sent this offer's link is skipped.
    /// </summary>
    public async Task<CheckoutMutationResult> InviteBuyersAsync(
        Guid ticketId, Guid userId, IReadOnlyList<string> inviteeEmails, Uri claimUrlBase, string culture, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        var ticketing = ticket is null ? null : await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct);
        if (ticket is null || offer is null || ticketing?.EffectiveSwitches.Resale != ResaleScheme.QueueAndDirected)
            return new("Tickets_Resale_NotFound");
        if (offer.State != TicketResaleOfferState.Open)
            return new("Tickets_Resale_InCheckout");
        if (await IsAdmittedAsync(ticket, ct))
            return new("Tickets_Resale_NotOfferable");
        var addresses = inviteeEmails.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count == 0 || addresses.Any(e => !LooksLikeEmail(e) || string.Equals(e, ticket.HolderEmail, StringComparison.OrdinalIgnoreCase)))
            return new("Tickets_Resale_InviteEmailInvalid");
        addresses.RemoveAll(e => offer.Invites.Any(i => string.Equals(i.Email, e, StringComparison.OrdinalIgnoreCase)));
        if (addresses.Count == 0)
            return new("Tickets_Resale_InviteAlready");
        // Counted over all of the ticket's offers, so withdrawing and offering again does not start a fresh allowance.
        if ((await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
                .Where(o => o.TicketId == ticket.Id).Sum(o => o.DirectedInviteCount) + addresses.Count > MaxInvitesPerTicket)
            return new("Tickets_Resale_InviteLimit");
        var now = clock.GetCurrentInstant();
        if (ticketing.IsClosed(now))
            return new("Tickets_Resale_Closed");
        if (!(await LoadStockAsync(ticketing, now, ct)).ResaleOpen)
            return new("Tickets_Resale_NotOpenYet");

        var links = addresses.Select(address =>
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return (Address: address, Token: token);
        }).ToList();
        offer.Invites = [.. offer.Invites, .. links.Select(l => new TicketResaleInvite { Email = l.Address, TokenHash = HashToken(l.Token), SentAt = now })];
        offer.DirectedInviteCount += links.Count;
        await repository.UpdateOfferAsync(offer, ct);

        var eventName = (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? string.Empty;
        foreach (var (address, token) in links)
            await email.SendAsync(emails.ResaleInvitation(
                address, ticket.HolderName, eventName, new Uri(claimUrlBase, token).ToString(), culture), ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInvited,
            $"Holder sent the link for ticket {ticket.Barcode} to {links.Count} more person(s), {offer.Invites.Count} in all; " +
            "the ticket stays in the queue meanwhile", userId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> CancelInviteAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        if (ticket is null || offer is null || offer.Invites.Count == 0)
            return new("Tickets_Resale_NotFound");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("Tickets_Resale_InCheckout");
        offer.Invites = [];
        await repository.UpdateOfferAsync(offer, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInviteCancelled,
            $"Invitations for ticket {ticket.Barcode} cancelled; it keeps its place in the queue", userId, ct);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>The ticket or a valid dependent was admitted: a companion can be on a separate order.</summary>
    private async Task<bool> IsAdmittedAsync(TicketCheckoutTicket ticket, CancellationToken ct)
    {
        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .Select(t => t.Barcode);
        return (await gateAdmissions.GetAdmittedAsync([ticket.Barcode, .. dependents], ct)).Count > 0;
    }

    private async Task<bool> IsAdmittedAsync(Guid ticketId, CancellationToken ct) =>
        await repository.GetTicketAsync(ticketId, ct) is { } ticket && await IsAdmittedAsync(ticket, ct);

    private async Task<(TicketCheckoutTicket? Ticket, TicketResaleOffer? Offer)> OwnOfferAsync(
        Guid ticketId, Guid userId, CancellationToken ct)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != userId || ticket.State != TicketCheckoutTicketState.Offered)
            return (null, null);
        var offer = (await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        return (ticket, offer);
    }

    /// <summary>
    /// Why the link can't buy for the signed-in buyer, or null when it was sent to their email, for this event, and
    /// resale is selling.
    /// </summary>
    private static string? InviteRefusal(TicketResaleOffer? offer, string tokenHash, BuyContext context) =>
        offer is null || offer.TicketingSettingsId != context.Ticketing.Id || offer.InviteFor(tokenHash) is not { } invite
            ? "Tickets_Resale_InviteNotFound"
            : !string.Equals(invite.Email, context.Buyer?.Email, StringComparison.OrdinalIgnoreCase)
                ? "Tickets_Resale_InviteOtherEmail"
                : !context.Ticketing.IsResaleSelling(context.Now) ? "Tickets_Resale_NotSelling" : null;

    /// <summary>The linked offer while it can still sell to this buyer: the link is theirs and the queue is open.</summary>
    private static TicketResaleOffer? DirectedOfferFor(TicketResaleOffer? offer, string tokenHash, BuyContext context) =>
        offer?.State == TicketResaleOfferState.Open && InviteRefusal(offer, tokenHash, context) is null
            ? offer
            : null;

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task ReserveOfferAsync(TicketResaleOffer offer, Guid orderId, CancellationToken ct)
    {
        offer.State = TicketResaleOfferState.Reserved;
        offer.ReservedByOrderId = orderId;
        await repository.UpdateOfferAsync(offer, ct);
    }

    /// <summary>The order will never pay: its reserved offers go back to the queue, keeping their place.</summary>
    private async Task ReleaseOffersAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        foreach (var offerId in order.Lines.Select(l => l.ResaleOfferId).OfType<Guid>())
        {
            var offer = await repository.GetOfferAsync(offerId, ct);
            if (offer is not { State: TicketResaleOfferState.Reserved } || offer.ReservedByOrderId != order.Id)
                continue;
            if ((await repository.GetTicketingSettingsAsync(offer.TicketingSettingsId, ct))?.EffectiveSwitches.Resale == ResaleScheme.Off)
            {
                await WithdrawSwitchedOffAsync(offer, null, ct);
                continue;
            }
            offer.State = TicketResaleOfferState.Open;
            offer.ReservedByOrderId = null;
            await repository.UpdateOfferAsync(offer, ct);
        }
    }

    /// <summary>
    /// Issues the paid order's tickets (once: the unique line index turns a second caller away), completes any resale
    /// it bought, and tells delivery. Safe to repeat: the reconcile job calls it again for a paid order a crash left
    /// without tickets or with a resale unfinished.
    /// </summary>
    private async Task CompletePaidAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var issuedNow = false;
        if ((await repository.GetTicketsForOrderAsync(order.Id, ct)).Count == 0
            && order.Lines.Any(l => l.Kind == OrderLineKind.Ticket))
        {
            await FlagDoubleHoldersAsync(order, ct);
            var now = clock.GetCurrentInstant();
            var ids = order.Lines.Select(_ => Guid.NewGuid()).ToList();
            var tickets = order.Lines
                .Select((line, index) => (line, index))
                .Where(x => x.line.Kind == OrderLineKind.Ticket)
                .Select(x => new TicketCheckoutTicket
                {
                    Id = ids[x.index],
                    TicketingSettingsId = order.TicketingSettingsId,
                    OrderId = order.Id,
                    LineIndex = x.index,
                    Kind = x.line.HolderKind ?? TicketHolderKind.Adult,
                    ParentTicketId = x.line.ParentLineIndex is { } parent ? ids[parent] : null,
                    HolderUserId = x.line.HolderUserId,
                    HolderName = x.line.HolderName ?? string.Empty,
                    HolderEmail = x.line.HolderEmail,
                    Barcode = NewBarcode(),
                    PricePaidMinor = x.line.AmountMinor,
                    WaveName = x.line.WaveName ?? string.Empty,
                    ClassProgramId = x.line.ClassProgramId,
                    ClassLabel = x.line.ClassLabel,
                    DiscountsMinor = x.line.DiscountsMinor,
                    State = TicketCheckoutTicketState.Valid,
                    IssuedAt = now,
                })
                .ToList();
            if (!await repository.AddTicketsAsync(tickets, ct))
                return;
            foreach (var holder in tickets.Select(t => t.HolderUserId).OfType<Guid>().Distinct())
                holdings.InvalidateHoldings(holder);
            issuedNow = true;
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.TicketsIssued,
                $"{tickets.Count} ticket(s) issued: {string.Join(", ", tickets.Select(t => t.Barcode))}", null, ct);
        }

        foreach (var offerId in order.Lines.Select(l => l.ResaleOfferId).OfType<Guid>())
            await CompleteResaleAsync(offerId, order, ct);

        if (issuedNow || !order.Lines.Any(l => l.Kind == OrderLineKind.Ticket))
            await HandOffToDeliveryAsync(order.Id, ct);
    }

    private Task HandOffToDeliveryAsync(Guid orderId, CancellationToken ct) =>
        TellDeliveryAsync(d => d.TicketsIssuedAsync(orderId, ct), orderId);

    /// <summary>A mail that cannot be queued is logged and never undoes the change it reports. False when one failed.</summary>
    private async Task<bool> TellDeliveryAsync(Func<ITicketDelivery, Task> tell, Guid subjectId)
    {
        var queued = true;
        foreach (var delivery in deliveries)
        {
            try
            {
                await tell(delivery);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ticket delivery for {SubjectId} failed", subjectId);
                queued = false;
            }
        }
        return queued;
    }

    /// <summary>
    /// Holding a ticket is checked before payment, but a lapsed order can still pay late after the person bought
    /// one of their own: the team then refunds one of the two.
    /// </summary>
    private async Task FlagDoubleHoldersAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        foreach (var (line, index) in order.Lines.Select((l, i) => (l, i + 1))
                     .Where(x => x.l is { Kind: OrderLineKind.Ticket, HolderUserId: not null } && x.l.HolderKind != TicketHolderKind.Child)
                     .DistinctBy(x => x.l.HolderUserId))
            if (await HoldsTicketAsync(order.TicketingSettingsId, line.HolderUserId!.Value, now, ct))
                await NeedsAttentionAsync(order.Id,
                    $"The holder of line {index} already held a ticket for this event; the ticketing team should refund one", ct);
    }

    /// <summary>
    /// The account a ticket for <paramref name="address"/> belongs to. Only a verified address attaches a ticket to an
    /// existing account; a single unverified row is replaced by a new account, as the attendee import does; an
    /// ambiguous address gives no account and the reason. The legal name stays on the line and ticket: as a display
    /// name it would become the account's public burner name.
    /// </summary>
    private async Task<(Guid? UserId, string? Problem)> ResolveAccountAsync(string address, CancellationToken ct)
    {
        var verified = (await userEmails.FindByAddressAsync(address, aliased: true, verifiedOnly: true, ct))
            .Select(r => r.UserId).Distinct().ToList();
        if (verified.Count == 1)
            return ((await users.GetUserInfoAsync(verified[0], ct))?.Id ?? verified[0], null);
        if (verified.Count > 1)
            return (null, "the address is verified on more than one account");
        var rows = await userEmails.FindByAddressAsync(address, aliased: true, verifiedOnly: false, ct);
        if (rows.Count > 1)
            return (null, "the address is unverified on more than one account");
        var provisioned = rows.Count == 1
            ? await provisioning.ReplaceUnverifiedEmailAsync(rows[0].UserId, rows[0].Id, address, displayName: null, ContactSource.Manual, ct)
            : await provisioning.FindOrCreateUserByEmailAsync(address, displayName: null, ContactSource.Manual, ct);
        return (provisioned.UserId, null);
    }

    /// <summary>On the order's timeline and in the audit log, where the ticketing team reviews what automation left them.</summary>
    private async Task NeedsAttentionAsync(Guid orderId, string detail, CancellationToken ct)
    {
        await TimelineAsync(orderId, TicketCheckoutOrderEventKind.TicketsIssued, detail, null, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, orderId, detail, JobName);
    }

    private async Task CompleteResaleAsync(Guid offerId, TicketCheckoutOrder buyerOrder, CancellationToken ct)
    {
        var offer = await repository.GetOfferAsync(offerId, ct);
        if (offer is null)
            return;
        if (offer.State == TicketResaleOfferState.Sold)
        {
            // A repeat after a crash finishes whatever the first run left: the old ticket and its children. The refund
            // is the evening batch's.
            if (offer.SoldToOrderId == buyerOrder.Id)
                await RetireResoldTicketAsync(offer, offer.DirectedUsedAt is not null, ct);
            return;
        }
        // A late payment finds its offer released, maybe taken by another order or withdrawn: its ticket is then fresh stock.
        if (offer.State == TicketResaleOfferState.Withdrawn
            || (offer.State == TicketResaleOfferState.Reserved && offer.ReservedByOrderId != buyerOrder.Id))
            return;

        var now = clock.GetCurrentInstant();
        // The queue also sells an invited ticket, so only an invited buyer's own order counts as using the invitation.
        var directed = offer.HasLiveInvite
            && offer.Invites.Any(i => string.Equals(i.Email, buyerOrder.BuyerEmail, StringComparison.OrdinalIgnoreCase));
        offer.State = TicketResaleOfferState.Sold;
        offer.SoldAt = now;
        offer.SoldToOrderId = buyerOrder.Id;
        offer.ReservedByOrderId = null;
        if (directed)
            offer.DirectedUsedAt = now;
        await repository.UpdateOfferAsync(offer, ct);

        await RetireResoldTicketAsync(offer, directed, ct);
        if (directed)
            await TimelineAsync(buyerOrder.Id, TicketCheckoutOrderEventKind.ResaleInviteUsed,
                "Bought through a resale invitation", buyerOrder.BuyerUserId, ct);
        await TellDeliveryAsync(d => d.ResaleSoldAsync(offer.TicketId, offer.RefundOwedMinor, ct), offer.TicketId);
    }

    /// <summary>Marks a sold offer's ticket <c>Resold</c> and voids its children; each step is skipped once done.</summary>
    private async Task RetireResoldTicketAsync(TicketResaleOffer offer, bool directed, CancellationToken ct)
    {
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return;
        var now = clock.GetCurrentInstant();
        if (old.State != TicketCheckoutTicketState.Resold)
        {
            old.State = TicketCheckoutTicketState.Resold;
            await repository.UpdateTicketAsync(old, ct);
            await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleSold,
                $"Ticket {old.Barcode} resold" + (directed ? " to the invited buyer" : string.Empty) +
                $"; {FormatMinor(offer.RefundOwedMinor)} goes back in the evening refund batch once the buyer's payment holds", null, ct);
        }
        foreach (var child in (await repository.GetTicketsForSettingsAsync(old.TicketingSettingsId, ct))
                     .Where(t => t.ParentTicketId == old.Id && t.State == TicketCheckoutTicketState.Valid))
        {
            child.State = TicketCheckoutTicketState.Void;
            child.VoidedAt = now;
            child.VoidReason = "Resold with the adult's ticket";
            await repository.UpdateTicketAsync(child, ct);
            if (child.OrderId != old.OrderId)
                await TimelineAsync(child.OrderId, TicketCheckoutOrderEventKind.Voided,
                    $"Ticket {child.Barcode} voided: its adult's ticket {old.Barcode} was resold", null, ct);
        }
    }

    /// <summary>
    /// Refunds the holder what they paid, under the offer's own id so a repeat finds the same refund and retries a
    /// platform call that threw. A refund that cannot even start is written as a failed refund on the holder's order,
    /// so the ticketing team sees what is owed. True when the refund started.
    /// </summary>
    private async Task<bool> SettleResaleRefundAsync(TicketResaleOffer offer, CancellationToken ct)
    {
        if (offer.RefundOwedMinor <= 0 || await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return false;

        var (result, refundId) = await RefundCoreAsync(old.OrderId, offer.RefundOwedMinor, "Ticket resold", null, ct, offer.Id);
        if (refundId is null)
        {
            logger.LogError("Resale refund for ticket {TicketId} could not start: {Refusal}", old.Id, result.Refusal);
            var why = Truncate(result.Refusal ?? "refused");
            await RecordResaleRefundNotSentAsync(offer, old, why,
                $"Resale refund of {FormatMinor(offer.RefundOwedMinor)} could not start ({why}); the ticketing team must refund it by hand", ct);
            return false;
        }
        if (offer.RefundId is null)
        {
            offer.RefundId = offer.Id;
            await repository.UpdateOfferAsync(offer, ct);
        }
        return true;
    }

    /// <summary>
    /// A failed refund under the offer's id: the batch never sends it later, and it stays among the failed refunds the
    /// reconciliation lists until the team settles it.
    /// </summary>
    private async Task RecordResaleRefundNotSentAsync(
        TicketResaleOffer offer, TicketCheckoutTicket old, string failureReason, string detail, CancellationToken ct)
    {
        await repository.AddRefundAsync(new TicketCheckoutRefund
        {
            Id = offer.Id,
            OrderId = old.OrderId,
            AmountMinor = offer.RefundOwedMinor,
            Reason = "Ticket resold",
            State = RefundState.Failed,
            FailureReason = Truncate(failureReason),
            CreatedAt = clock.GetCurrentInstant(),
        }, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, old.OrderId, detail, JobName);
        await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.RefundSettled, detail, null, ct);
        offer.RefundId = offer.Id;
        await repository.UpdateOfferAsync(offer, ct);
    }

    /// <summary>
    /// The evening batch: refunds each holder whose ticket sold in resale at least <see cref="ResaleRefundHold"/> ago to
    /// a buyer whose order is still paid with nothing refunded, being refunded or charged back. A sale that did not hold is left for
    /// the ticketing team as a failed refund. One audit row per event carries the batch's count and total.
    /// </summary>
    public async Task<ResaleRefundBatch> RunResaleRefundBatchAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant() - ResaleRefundHold;
        var batch = new ResaleRefundBatch(0, 0, 0, 0);
        // A cancelled event refunds both the old holder's and the buyer's order in full, so a resale refund would pay twice.
        foreach (var ticketing in (await repository.ListTicketingSettingsAsync(ct)).Where(e => e.CancelledAt is null))
        {
            var (started, startedMinor, heldBack, waiting) = (0, 0L, 0, 0);
            foreach (var offer in (await repository.GetOffersForSettingsAsync(ticketing.Id, ct))
                         .Where(o => o is { State: TicketResaleOfferState.Sold, RefundId: null, RefundOwedMinor: > 0 })
                         .OrderBy(o => o.SoldAt))
            {
                if (offer.SoldAt > cutoff)
                {
                    waiting++;
                    continue;
                }
                var buyerOrder = offer.SoldToOrderId is { } buyerOrderId ? await repository.GetOrderAsync(buyerOrderId, ct) : null;
                try
                {
                    // A missed webhook must not pay out: both orders are read from their platform first, so a refund or
                    // chargeback only the platform knows of still holds the refund back or lowers what can be refunded.
                    if (buyerOrder is not null)
                        await RecoverAsync(buyerOrder, ct);
                    if (await repository.GetTicketAsync(offer.TicketId, ct) is { } old
                        && await repository.GetOrderAsync(old.OrderId, ct) is { } holderOrder)
                        await RecoverAsync(holderOrder, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Resale refund for offer {OfferId} waits: its orders could not be read from the platform", offer.Id);
                    waiting++;
                    continue;
                }
                if (buyerOrder is not { State: TicketCheckoutOrderState.Paid, RefundedMinor: 0, ChargedBackMinor: 0 }
                    || (await repository.GetRefundsAsync(buyerOrder.Id, ct)).Any(r => r.State != RefundState.Failed))
                {
                    if (await HoldBackResaleRefundAsync(offer, buyerOrder, ct))
                        heldBack++;
                    continue;
                }
                try
                {
                    if (await SettleResaleRefundAsync(offer, ct))
                    {
                        started++;
                        startedMinor += offer.RefundOwedMinor;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Resale refund for offer {OfferId} failed; the next batch retries", offer.Id);
                }
            }
            if (started + heldBack > 0)
                await auditLog.LogAsync(TicketsAuditActions.TicketResaleRefundBatch, TicketingSettingsAuditEntityType, ticketing.Id,
                    $"Evening resale refund batch: {started} refund(s) started, {FormatMinor(startedMinor)} in total; " +
                    $"{heldBack} held back, each with its own entry saying why; " +
                    $"{waiting} left for a later batch", JobName);
            batch = new(batch.Started + started, batch.StartedMinor + startedMinor, batch.HeldBack + heldBack, batch.Waiting + waiting);
        }
        return batch;
    }

    /// <summary>Held back for good: the ticketing team decides, and a later batch never pays it on its own.</summary>
    private async Task<bool> HoldBackResaleRefundAsync(TicketResaleOffer offer, TicketCheckoutOrder? buyerOrder, CancellationToken ct)
    {
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return false;
        if ((await repository.GetRefundsAsync(old.OrderId, ct)).Any(r => r.Id == offer.Id))
        {
            // Asked for before a crash kept the offer from recording it: that refund is already on its way.
            offer.RefundId = offer.Id;
            await repository.UpdateOfferAsync(offer, ct);
            return false;
        }
        var why = $"{RefundHeldBack}: the buyer's order {buyerOrder?.Reference ?? "unknown"} is " +
            (buyerOrder is null ? "gone" : $"{buyerOrder.State} with {FormatMinor(buyerOrder.RefundedMinor)} refunded and {FormatMinor(buyerOrder.ChargedBackMinor)} charged back");
        await RecordResaleRefundNotSentAsync(offer, old, why,
            $"{why}; the ticketing team decides on the {FormatMinor(offer.RefundOwedMinor)} owed for ticket {old.Barcode}", ct);
        return true;
    }

    /// <summary>Finishes what a crash left behind: paid orders without their tickets, resale refund or delivery, and refunds that never reached the platform.</summary>
    private async Task<int> RepairPaidAsync(CancellationToken ct)
    {
        var repaired = 0;
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
        {
            var eventTickets = await repository.GetTicketsForSettingsAsync(ticketing.Id, ct);
            var issued = eventTickets.Select(t => t.OrderId).ToHashSet();
            var orders = await repository.GetOrdersForSettingsAsync(ticketing.Id, ct);
            // A sold offer whose old ticket or a child riding on it is still live: the resale stopped halfway.
            var unfinished = (await repository.GetOffersForSettingsAsync(ticketing.Id, ct))
                .Where(o => o.State == TicketResaleOfferState.Sold && eventTickets.Any(t =>
                    (t.Id == o.TicketId && t.State != TicketCheckoutTicketState.Resold)
                    || (t.ParentTicketId == o.TicketId && t.State == TicketCheckoutTicketState.Valid)))
                .Select(o => o.SoldToOrderId)
                .ToHashSet();
            foreach (var order in orders.Where(o => o.State == TicketCheckoutOrderState.Paid
                         && ((!issued.Contains(o.Id) && o.Lines.Any(l => l.Kind == OrderLineKind.Ticket)) || unfinished.Contains(o.Id))))
            {
                try
                {
                    await CompletePaidAsync(order, ct);
                    repaired++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Finishing paid order {OrderId} failed; next run retries", order.Id);
                }
            }

            // Tickets or a donation receipt never handed to delivery. Delivery dedupes a repeat;
            // the wait lets a first hand-off still in its queue finish, and the window keeps old orders out.
            var now = clock.GetCurrentInstant();
            foreach (var order in orders.Where(o => deliveries.Any() && o is { State: TicketCheckoutOrderState.Paid, PaidAt: { } paidAt }
                         && (issued.Contains(o.Id) || !o.Lines.Any(l => l.Kind == OrderLineKind.Ticket))
                         && paidAt <= now - DeliveryGrace && paidAt > now - DeliveryRepairWindow))
            {
                if ((await repository.GetOrderEventsAsync(order.Id, ct)).Any(e =>
                        e.Kind is TicketCheckoutOrderEventKind.TicketsEmailed or TicketCheckoutOrderEventKind.TicketMailSkipped))
                    continue;
                await HandOffToDeliveryAsync(order.Id, ct);
            }

            // A refund whose platform call threw may or may not exist there; repeating it under its id settles which.
            foreach (var unsent in (await repository.GetRefundsForSettingsAsync(ticketing.Id, ct))
                         .Where(r => r is { State: RefundState.Pending, PlatformRefundId: null }))
            {
                try
                {
                    await RefundCoreAsync(unsent.OrderId, unsent.AmountMinor, unsent.Reason, null, ct, unsent.Id);
                    if ((await repository.GetRefundsAsync(unsent.OrderId, ct)).FirstOrDefault(r => r.Id == unsent.Id)
                        is not { State: RefundState.Pending, PlatformRefundId: null })
                        repaired++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Resending refund {RefundId} failed; next run retries", unsent.Id);
                }
            }
        }
        return repaired;
    }

    /// <summary>
    /// Repairs offers held by an order that will never pay or already paid, and tells each holder whose ticket is
    /// still in resale once sales have closed. Returns how many changed.
    /// </summary>
    private async Task<int> SweepOffersAsync(CancellationToken ct)
    {
        var changed = 0;
        var now = clock.GetCurrentInstant();
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
        {
            // A cancelled event is closed too, but its holders are refunded by the cancel sweep, not told there is no refund.
            var closed = ticketing.CancelledAt is null && ticketing.IsClosed(now);
            foreach (var offer in await repository.GetOffersForSettingsAsync(ticketing.Id, ct))
            {
                if (closed && offer is { State: TicketResaleOfferState.Open, UnsoldNotifiedAt: null }
                    && await NotifyUnsoldAsync(offer, now, ct))
                    changed++;
                if (offer is { State: TicketResaleOfferState.Reserved, ReservedByOrderId: { } orderId }
                         && await repository.GetOrderAsync(orderId, ct) is { } holder
                         && !holder.IsOpen)
                {
                    if (holder.State == TicketCheckoutOrderState.Paid)
                        await CompletePaidAsync(holder, ct);
                    else
                        await ReleaseOffersAsync(holder, ct);
                    changed++;
                }
            }
        }
        return changed;
    }

    /// <summary>The offer stays in the queue (sales may reopen); the ticket admits its holder once sales are closed.</summary>
    private async Task<bool> NotifyUnsoldAsync(TicketResaleOffer offer, Instant now, CancellationToken ct)
    {
        // Marked only once the mail is queued, so a failed hand-off is tried again by the next sweep; the delivery id
        // keeps a repeat from mailing twice.
        if (!await TellDeliveryAsync(d => d.ResaleUnsoldAsync(offer.TicketId, ct), offer.TicketId))
            return false;
        offer.UnsoldNotifiedAt = now;
        await repository.UpdateOfferAsync(offer, ct);
        if (await repository.GetTicketAsync(offer.TicketId, ct) is { } ticket)
            await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleUnsold,
                $"Sales closed with ticket {ticket.Barcode} unsold in the resale queue: the holder is told there is no refund and the ticket still admits them",
                null, ct);
        return true;
    }

    internal const int MaxReasonLength = 500;

    public async Task<CheckoutMutationResult> RenameTicketAsync(
        Guid ticketId, string holderName, string holderEmail, string reason, bool overrideRules, Guid actorUserId, CancellationToken ct = default)
    {
        holderName = holderName.Trim();
        holderEmail = holderEmail.Trim();
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        if (holderName.Length == 0 || !LooksLikeEmail(holderEmail))
            return new("A rename needs the holder's legal name and email.");
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult)
            return new("Only a valid adult ticket can be renamed.");

        var fromUserId = ticket.HolderUserId;
        var existing = await users.GetByEmailOrAlternateAsync(holderEmail, ct);
        var samePerson = existing is not null && existing.Id == fromUserId;
        // Correcting the same person's legal name is always allowed; moving the ticket to someone else is the exception.
        if (!samePerson)
        {
            if (!overrideRules && (ticket.ClassLabel is not null || ticket.DiscountsMinor > 0))
                return new("Only a full price ticket can move to another person; this one was bought at a class price or with discounts.");
            if ((await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
                .Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
                return new("Void this ticket's child and companion tickets before moving it to another person.");
            if (existing is not null && await HoldsTicketAsync(ticket.TicketingSettingsId, existing.Id, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
        }
        // Only once the rename is allowed, so a refused move leaves no account behind.
        var holderUserId = existing?.Id ?? Guid.Empty;
        if (existing is null)
        {
            var (resolved, problem) = await ResolveAccountAsync(holderEmail, ct);
            if (resolved is not { } resolvedId)
                return new($"That email cannot take the ticket: {problem}.");
            holderUserId = resolvedId;
            // The resolver also matches aliases, which the lookup above may not.
            if (holderUserId != fromUserId && await HoldsTicketAsync(ticket.TicketingSettingsId, holderUserId, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
            samePerson = holderUserId == fromUserId;
        }

        ticket.HolderUserId = holderUserId;
        ticket.HolderName = holderName;
        ticket.HolderEmail = holderEmail;
        await repository.UpdateTicketAsync(ticket, ct);
        holdings.InvalidateHoldings(holderUserId);
        // The order line follows the ticket, so the new holder can open the order and the old one no longer can.
        var order = await repository.GetOrderAsync(ticket.OrderId, ct);
        var line = order?.Lines.FirstOrDefault(l =>
            l.Kind == OrderLineKind.Ticket && l.HolderKind is null or TicketHolderKind.Adult && l.HolderUserId == fromUserId);
        if (order is not null && line is not null)
        {
            line.HolderUserId = holderUserId;
            line.HolderName = holderName;
            line.HolderEmail = holderEmail;
            await repository.UpdateOrderAsync(order, ct);
        }

        var why = reason.Trim();
        // Only a move of a reduced price ticket steps past a rule; anything else is a plain rename.
        var overridden = overrideRules && !samePerson && (ticket.ClassLabel is not null || ticket.DiscountsMinor > 0);
        var what = (samePerson ? "name corrected" : "moved to another person") + (overridden ? " by an override" : string.Empty);
        // Names stay out of the timeline (erasure leaves it); the audit rows link the people by id.
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.Renamed,
            $"Ticket {ticket.Barcode} {what} by the ticketing team: {why}", actorUserId, ct);
        var description = (overridden ? "Override: " : string.Empty) + $"Ticket {ticket.Barcode} {what}" +
            (samePerson ? string.Empty : $" (from user {fromUserId?.ToString() ?? "none"} to user {holderUserId})") + $": {why}";
        var action = overridden ? TicketsAuditActions.TicketOverride : TicketsAuditActions.TicketCheckoutTicketRenamed;
        await auditLog.LogAsync(action, AuditEntityType, ticket.OrderId,
            description, actorUserId, holderUserId, AuditSubjectTypes.User);
        if (!samePerson && fromUserId is { } previous)
            await auditLog.LogAsync(action, AuditEntityType, ticket.OrderId,
                description, actorUserId, previous, AuditSubjectTypes.User);
        await TellDeliveryAsync(d => d.TicketRenamedAsync(ticket.Id, ct), ticket.Id);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> VoidTicketAsync(
        Guid ticketId, string reason, long refundMinor, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        if (refundMinor < 0)
            return new("The refund cannot be negative.");
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (!ticket.IsHeld)
            return new($"The ticket is already {ticket.State}.");
        if (ticket.ParentTicketId is { } parentId && (await repository.GetTicketAsync(parentId, ct))?.State == TicketCheckoutTicketState.Offered)
            return new("The adult's ticket is in the resale queue and its refund includes this one: withdraw it from the queue first.");

        var offer = (await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        if (offer?.State == TicketResaleOfferState.Reserved)
            return new("A buyer's order is paying for this ticket right now; try again once it settles.");

        var why = reason.Trim();
        // Refund first: a refund the platform turns down leaves the ticket working, so the team can try again.
        if (refundMinor > 0)
        {
            var (refunded, _) = await RefundCoreAsync(ticket.OrderId, refundMinor, $"Ticket {ticket.Barcode} voided: {why}", actorUserId, ct);
            if (!refunded.Succeeded)
                return new($"{refunded.Refusal} Nothing was voided.");
        }

        var dependents = await VoidWithDependentsAsync(ticket, offer, why, actorUserId,
            refundMinor > 0 ? $"; {FormatMinor(refundMinor)} refunded" : "; no refund: no money goes back", ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} voided" + (dependents > 0 ? $" with {dependents} dependent ticket(s)" : string.Empty) +
            (offer is not null ? ", withdrawing its resale offer" : string.Empty) +
            (refundMinor > 0 ? $", refunding {FormatMinor(refundMinor)}" : ", with no refund") + $": {why}",
            actorUserId, ticket.HolderUserId, ticket.HolderUserId is null ? null : AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>Voids the ticket and its valid dependents and withdraws <paramref name="offer"/>; returns how many dependents went with it.</summary>
    private async Task<int> VoidWithDependentsAsync(
        TicketCheckoutTicket ticket, TicketResaleOffer? offer, string why, Guid? actorUserId, string note, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (offer is not null)
        {
            offer.State = TicketResaleOfferState.Withdrawn;
            offer.WithdrawnAt = now;
            offer.Invites = [];
            await repository.UpdateOfferAsync(offer, ct);
        }

        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        foreach (var voided in dependents.Prepend(ticket))
        {
            voided.State = TicketCheckoutTicketState.Void;
            voided.VoidedAt = now;
            voided.VoidReason = voided == ticket ? why : "Voided with the adult's ticket";
            await repository.UpdateTicketAsync(voided, ct);
            await TimelineAsync(voided.OrderId, TicketCheckoutOrderEventKind.Voided,
                $"Ticket {voided.Barcode} voided: {voided.VoidReason}" +
                (voided == ticket ? note + (offer is not null ? "; taken out of the resale queue" : string.Empty) : string.Empty),
                actorUserId, ct);
        }
        return dependents.Count;
    }

    /// <summary>The override for a ticket voided by mistake: it works again, whatever was refunded for it.</summary>
    public async Task<CheckoutMutationResult> RestoreTicketAsync(
        Guid ticketId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.State != TicketCheckoutTicketState.Void)
            return new("Only a voided ticket can be restored.");
        if ((await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct))?.CancelledAt is not null)
            return new("The event is cancelled; its tickets stay void.");
        if (ticket.ParentTicketId is { } parentId && (await repository.GetTicketAsync(parentId, ct))?.State != TicketCheckoutTicketState.Valid)
            return new("Restore the adult's ticket first.");
        if (ticket.HolderUserId is { } holder && await HoldsTicketAsync(ticket.TicketingSettingsId, holder, clock.GetCurrentInstant(), ct))
            return new("The holder already holds another ticket for this event.");

        var why = reason.Trim();
        var wasVoided = ticket.VoidReason;
        ticket.State = TicketCheckoutTicketState.Valid;
        ticket.VoidedAt = null;
        ticket.VoidReason = null;
        await repository.UpdateTicketAsync(ticket, ct);
        if (ticket.HolderUserId is { } holderId)
            holdings.InvalidateHoldings(holderId);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.Restored,
            $"Ticket {ticket.Barcode} restored by an override: {why} (it was voided: {wasVoided})", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketOverride, AuditEntityType, ticket.OrderId,
            $"Override: ticket {ticket.Barcode} restored after it was voided ({wasVoided}): {why}",
            actorUserId, ticket.HolderUserId, ticket.HolderUserId is null ? null : AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> IssueCompanionAsync(
        Guid parentTicketId, string holderName, string? holderEmail, string reason, bool overrideCapacity, Guid actorUserId, CancellationToken ct = default)
    {
        holderName = holderName.Trim();
        holderEmail = string.IsNullOrWhiteSpace(holderEmail) ? null : holderEmail.Trim();
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        if (holderName.Length == 0 || (holderEmail is not null && !LooksLikeEmail(holderEmail)))
            return new("A companion needs a legal name; the email is optional but must be valid.");
        return (await IssueCompanionCoreAsync(parentTicketId, holderName, holderEmail, async c =>
        {
            if (holderEmail is null)
                return (null, null);
            var (resolved, problem) = await ResolveAccountAsync(holderEmail, c);
            return resolved is null
                ? (null, $"That email cannot take the ticket: {problem}. Leave it empty to issue the companion without an account.")
                : (resolved, null);
        }, reason.Trim(), overrideCapacity, actorUserId, ct)).Result;
    }

    /// <summary>
    /// Every check that needs no companion account runs before <paramref name="holder"/> is asked for one, so a refused
    /// issue leaves no account behind.
    /// </summary>
    private async Task<(CheckoutMutationResult Result, TicketCheckoutTicket? Companion)> IssueCompanionCoreAsync(
        Guid parentTicketId, string holderName, string? holderEmail,
        Func<CancellationToken, Task<(Guid? UserId, string? Refusal)>> holder, string why, bool overrideCapacity, Guid actorUserId,
        CancellationToken ct)
    {
        var parent = await repository.GetTicketAsync(parentTicketId, ct);
        if (parent is null)
            return (new("That ticket does not exist."), null);
        if (parent.State != TicketCheckoutTicketState.Valid || parent.Kind != TicketHolderKind.Adult || parent.HolderUserId is not { } parentHolderId)
            return (new("A companion attaches to a valid adult ticket with a holder."), null);
        var ticketing = await repository.GetTicketingSettingsAsync(parent.TicketingSettingsId, ct);
        if (ticketing is null)
            return (new("That ticket's ticketing settings no longer exist."), null);
        var now = clock.GetCurrentInstant();
        var eventTickets = await repository.GetTicketsForSettingsAsync(parent.TicketingSettingsId, ct);
        if (eventTickets.Any(t => t.ParentTicketId == parent.Id && t.Kind == TicketHolderKind.Companion && t.State == TicketCheckoutTicketState.Valid))
            return (new("This ticket already has a companion."), null);
        if (ticketing.CancelledAt is not null)
            return (new("The event is cancelled."), null);
        var overCapacity = (await LoadStockAsync(ticketing, now, ct)).Held >= ticketing.Capacity;
        if (overCapacity && !overrideCapacity)
            return (new("The event is at capacity."), null);

        var (holderUserId, holderRefusal) = await holder(ct);
        if (holderRefusal is not null)
            return (new(holderRefusal), null);
        if (holderUserId is { } companionId && (companionId == parentHolderId || eventTickets.Any(t =>
                t.HolderUserId == companionId && t.IsHeld)))
            return (new("That person already holds a ticket for this event."), null);
        var parentOrder = await repository.GetOrderAsync(parent.OrderId, ct);
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            BuyerUserId = parentHolderId,
            BuyerName = parent.HolderName,
            BuyerEmail = parent.HolderEmail ?? parentOrder?.BuyerEmail ?? string.Empty,
            PlatformKey = ticketing.PlatformKey,
            State = TicketCheckoutOrderState.Paid,
            Culture = parentOrder?.Culture ?? "en",
            TotalMinor = 0,
            PaidAt = now,
            CreatedAt = now,
            ExpiresAt = now,
            UpdatedAt = now,
            Lines =
            [
                new TicketCheckoutOrderLine
                {
                    Kind = OrderLineKind.Ticket,
                    HolderKind = TicketHolderKind.Companion,
                    Description = "Companion ticket",
                    HolderName = holderName,
                    HolderEmail = holderEmail,
                    HolderUserId = holderUserId,
                    WaveName = parent.WaveName,
                },
            ],
        };
        await repository.AddOrderAsync(order, ct);
        var companion = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            OrderId = order.Id,
            Kind = TicketHolderKind.Companion,
            ParentTicketId = parent.Id,
            HolderUserId = holderUserId,
            HolderName = holderName,
            HolderEmail = holderEmail,
            Barcode = NewBarcode(),
            WaveName = parent.WaveName,
            State = TicketCheckoutTicketState.Valid,
            IssuedAt = now,
        };
        await repository.AddTicketsAsync([companion], ct);
        if (holderUserId is { } companionHolder)
            holdings.InvalidateHoldings(companionHolder);

        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} for ticket {parent.Barcode}: {why}", actorUserId, ct);
        await TimelineAsync(parent.OrderId, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} attached to ticket {parent.Barcode}", actorUserId, ct);
        var overridden = overCapacity && overrideCapacity;
        await auditLog.LogAsync(overridden ? TicketsAuditActions.TicketOverride : TicketsAuditActions.TicketCompanionIssued, AuditEntityType, order.Id,
            (overridden ? "Override: past capacity, c" : "C") + $"ompanion ticket {companion.Barcode} issued for ticket {parent.Barcode}: {why}",
            actorUserId, parentHolderId, AuditSubjectTypes.User);
        await HandOffToDeliveryAsync(order.Id, ct);
        return (CheckoutMutationResult.Ok, companion);
    }

    private static CheckoutMutationResult? ReasonRefusal(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? new("Give a reason; it goes on the order's timeline and the audit log.")
        : reason.Trim().Length > MaxReasonLength ? new($"Keep the reason under {MaxReasonLength} characters.")
        : null;

    // Companion requests (B8): the holder asks, the ticketing team approves or declines. The holder's note never goes
    // on the timeline or the audit log, which many people read; it stays on the request.

    private const int MaxCompanionNoteLength = 500;

    public async Task<CheckoutMutationResult> RequestCompanionAsync(
        Guid ticketId, Guid holderUserId, string companionEmail, string note, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != holderUserId)
            return new("Tickets_Resale_NotFound");
        var ticketing = await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct);
        var requests = await repository.GetCompanionRequestsForSettingsAsync(ticket.TicketingSettingsId, ct);
        if (!CanAskCompanion(ticket, ticketing, await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct), requests))
            return new("Tickets_Companion_NotAllowed");
        note = note.Trim();
        if (note.Length == 0 || note.Length > MaxCompanionNoteLength)
            return new("Tickets_Companion_NoteRequired");
        var now = clock.GetCurrentInstant();
        var (companion, problem) = await FindOtherAsync(companionEmail.Trim(), holderUserId, ticket.TicketingSettingsId, now, ct);
        if (problem is not null)
            return new("Tickets_Companion_" + problem);

        var request = new TicketCompanionRequest
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticket.TicketingSettingsId,
            TicketId = ticket.Id,
            HolderUserId = holderUserId,
            CompanionUserId = companion!.Id,
            Note = note,
            State = TicketCompanionRequestState.Pending,
            RequestedAt = now,
        };
        await repository.AddCompanionRequestAsync(request, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.CompanionRequested,
            $"Holder asked for a companion on ticket {ticket.Barcode}; waiting for the ticketing team", holderUserId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> WithdrawCompanionRequestAsync(Guid ticketId, Guid holderUserId, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != holderUserId)
            return new("Tickets_Resale_NotFound");
        var pending = (await repository.GetCompanionRequestsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .FirstOrDefault(r => r.TicketId == ticketId && r.State == TicketCompanionRequestState.Pending);
        if (pending is null)
            return new("Tickets_Companion_NothingPending");
        pending.State = TicketCompanionRequestState.Withdrawn;
        pending.DecidedAt = clock.GetCurrentInstant();
        await repository.UpdateCompanionRequestAsync(pending, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.CompanionRequestDecided,
            $"Holder withdrew the companion request on ticket {ticket.Barcode}", holderUserId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<IReadOnlyList<CompanionRequestRow>> GetCompanionRequestsAsync(Guid settingsId, CancellationToken ct = default)
    {
        var tickets = (await repository.GetTicketsForSettingsAsync(settingsId, ct)).ToDictionary(t => t.Id);
        var rows = new List<CompanionRequestRow>();
        foreach (var request in await repository.GetCompanionRequestsForSettingsAsync(settingsId, ct))
        {
            tickets.TryGetValue(request.TicketId, out var ticket);
            var companion = await users.GetUserInfoAsync(request.CompanionUserId, ct);
            rows.Add(new CompanionRequestRow(request, ticket?.HolderName ?? string.Empty,
                companion?.FullName ?? string.Empty, ticket?.Barcode ?? string.Empty, ticket?.OrderId ?? Guid.Empty));
        }
        return rows;
    }

    public async Task<CheckoutMutationResult> DecideCompanionRequestAsync(
        Guid requestId, bool approve, string? note, Guid actorUserId, CancellationToken ct = default)
    {
        var request = await repository.GetCompanionRequestAsync(requestId, ct);
        if (request is null)
            return new("That request does not exist.");
        if (request.State == TicketCompanionRequestState.Withdrawn)
            return new("That request was withdrawn.");
        if (request.State != TicketCompanionRequestState.Pending)
            return new($"That request was already {(request.State == TicketCompanionRequestState.Approved ? "approved" : "declined")}.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > MaxCompanionNoteLength)
            return new($"Keep the note under {MaxCompanionNoteLength} characters.");
        if (!approve && note is null)
            return new("Say why it is declined; the holder sees it.");

        TicketCheckoutTicket? companionTicket = null;
        if (approve)
        {
            var (companion, problem) = await CheckOtherAsync(
                request.CompanionUserId, request.HolderUserId, request.TicketingSettingsId, clock.GetCurrentInstant(), ct);
            if (problem is not null)
                return new(problem switch
                {
                    "Holds" => "The companion now holds a ticket of their own; decline the request.",
                    "NeedsName" => "The companion has no legal name in Humans yet; ask them to add it, then approve.",
                    _ => "The companion's account is gone; decline the request.",
                });
            var (result, issued) = await IssueCompanionCoreAsync(request.TicketId, companion!.FullName, companion.Email,
                _ => Task.FromResult<(Guid?, string?)>((companion.Id, null)),
                "approved companion request" + (note is null ? string.Empty : $": {note}"), overrideCapacity: false, actorUserId, ct);
            if (!result.Succeeded)
                return result;
            companionTicket = issued;
        }

        request.State = approve ? TicketCompanionRequestState.Approved : TicketCompanionRequestState.Declined;
        request.DecidedAt = clock.GetCurrentInstant();
        request.DecidedByUserId = actorUserId;
        request.DecisionNote = note;
        request.CompanionTicketId = companionTicket?.Id;
        await repository.UpdateCompanionRequestAsync(request, ct);

        var parent = await repository.GetTicketAsync(request.TicketId, ct);
        var summary = $"Companion request on ticket {parent?.Barcode} {(approve ? "approved" : "declined")}" +
            (note is null ? string.Empty : $": {note}");
        if (parent is not null)
            await TimelineAsync(parent.OrderId, TicketCheckoutOrderEventKind.CompanionRequestDecided, summary, actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCompanionRequestDecided, CompanionRequestAuditEntityType, request.Id,
            summary, actorUserId, request.HolderUserId, AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    internal const string CompanionRequestAuditEntityType = "TicketCompanionRequest";

    private static bool CanAskCompanion(
        TicketCheckoutTicket ticket, TicketingSettings? ticketing,
        IReadOnlyList<TicketCheckoutTicket> eventTickets, IReadOnlyList<TicketCompanionRequest> requests) =>
        ticket is { State: TicketCheckoutTicketState.Valid, Kind: TicketHolderKind.Adult, HolderUserId: not null }
        && ticketing is { Mode: not TicketSalesMode.Closed, EffectiveSwitches.CompanionTicketsOn: true }
        && !eventTickets.Any(t => t.ParentTicketId == ticket.Id && t.Kind == TicketHolderKind.Companion && t.State == TicketCheckoutTicketState.Valid)
        && !requests.Any(r => r.TicketId == ticket.Id && r.State == TicketCompanionRequestState.Pending);

    /// <summary>Kept under the reconcile job's four minute lock, leaving room for its other work.</summary>
    private static readonly TimeSpan CancelSweepBudget = TimeSpan.FromMinutes(2);

    public async Task<CheckoutMutationResult> WithdrawOfferForHolderAsync(
        Guid offerId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var offer = await repository.GetOfferAsync(offerId, ct);
        if (offer is null || offer.State is not (TicketResaleOfferState.Open or TicketResaleOfferState.Reserved))
            return new("That ticket is not waiting in the resale queue.");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("A buyer's order is paying for this ticket right now; try again once it settles.");

        offer.State = TicketResaleOfferState.Withdrawn;
        offer.WithdrawnAt = clock.GetCurrentInstant();
        offer.Invites = [];
        await repository.UpdateOfferAsync(offer, ct);
        var ticket = await repository.GetTicketAsync(offer.TicketId, ct);
        if (ticket?.State == TicketCheckoutTicketState.Offered)
        {
            ticket.State = TicketCheckoutTicketState.Valid;
            await repository.UpdateTicketAsync(ticket, ct);
        }

        var why = reason.Trim();
        var barcode = ticket?.Barcode ?? offer.TicketId.ToString();
        if (ticket is not null)
            await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleWithdrawn,
                $"Ticket {barcode} taken out of the resale queue by the ticketing team: {why}", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawnByTeam, AuditEntityType, ticket?.OrderId ?? offer.Id,
            $"Ticket {barcode} taken out of the resale queue for its holder: {why}",
            actorUserId, offer.HolderUserId, AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> CancelEventAsync(
        Guid settingsId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var ticketing = await repository.GetTicketingSettingsAsync(settingsId, ct);
        if (ticketing is null)
            return new("Those ticketing settings no longer exist.");
        if (ticketing.CancelledAt is not null)
            return new("The event is already cancelled.");

        var now = clock.GetCurrentInstant();
        var before = ticketing.Mode;
        ticketing.Mode = TicketSalesMode.Closed;
        ticketing.ResaleOpenedAt = null;
        ticketing.CancelledAt = now;
        ticketing.CancelledByUserId = actorUserId;
        ticketing.CancelReason = reason.Trim();
        ticketing.UpdatedAt = now;
        await repository.SaveTicketingSettingsAsync(ticketing, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketSalesEventCancelled, TicketingSettingsAuditEntityType, ticketing.Id,
            $"Event cancelled (sales were {before}): every ticket is voided and every paid order refunded what its tickets cost. " +
            $"Reason: {ticketing.CancelReason}", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CancellationProgress?> GetCancellationProgressAsync(Guid settingsId, CancellationToken ct = default)
    {
        var ticketing = await repository.GetTicketingSettingsAsync(settingsId, ct);
        return ticketing is null ? null : (await LoadCancellationAsync(ticketing, ct)).Progress;
    }

    /// <summary>
    /// Works the cancelled events' orders until the budget runs out; the next run carries on. Each order is safe to
    /// repeat: what is already void stays void, and the cancellation's own refund is asked for once.
    /// </summary>
    private async Task<int> SweepCancelledEventsAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var changed = 0;
        foreach (var ticketing in (await repository.ListTicketingSettingsAsync(ct)).Where(e => e.CancelledAt is not null))
        {
            var state = await LoadCancellationAsync(ticketing, ct);
            foreach (var order in state.ToDo)
            {
                if (watch.Elapsed > CancelSweepBudget)
                    return changed;
                try
                {
                    if (await CancelOrderAsync(ticketing, order, state, ct))
                        changed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Cancelling order {OrderId} of cancelled event {TicketingSettingsId} failed; next run retries",
                        order.Id, ticketing.Id);
                }
            }
        }
        return changed;
    }

    /// <summary>False while a buyer's unpaid order holds one of the order's tickets in resale: it waits for that to settle.</summary>
    private async Task<bool> CancelOrderAsync(
        TicketingSettings ticketing, TicketCheckoutOrder order, CancellationState state, CancellationToken ct)
    {
        var live = state.Tickets[order.Id].Where(t => t.IsHeld).ToList();
        var offers = state.Offers.Where(o => live.Any(t => t.Id == o.TicketId)
            && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved).ToList();
        if (offers.Any(o => o.State == TicketResaleOfferState.Reserved))
            return false;

        var why = $"Event cancelled: {ticketing.CancelReason}";
        var now = clock.GetCurrentInstant();
        foreach (var offer in offers)
        {
            offer.State = TicketResaleOfferState.Withdrawn;
            offer.WithdrawnAt = now;
            offer.Invites = [];
            await repository.UpdateOfferAsync(offer, ct);
        }
        foreach (var ticket in live)
        {
            ticket.State = TicketCheckoutTicketState.Void;
            ticket.VoidedAt = now;
            ticket.VoidReason = Truncate(why);
            await repository.UpdateTicketAsync(ticket, ct);
            if (ticket.HolderUserId is { } holder)
                holdings.InvalidateHoldings(holder);
        }

        // A repeat after a stop part way finds the refund already asked for, so the email still names it.
        var (refundMinor, refundByHand) = state.CancelRefund(order) is { } asked
            ? (asked.AmountMinor, asked.State is RefundState.Failed or RefundState.ManualRequired)
            : (state.Refundable(order), false);
        string? refundNote = null;
        if (refundMinor > 0 && !state.HasCancelRefund(order))
        {
            var cancelRefundId = CancellationState.CancelRefundId(ticketing.Id, order.Id);
            var (result, refundId) = await RefundCoreAsync(order.Id, refundMinor, Truncate(why), null, ct, cancelRefundId);
            if (!result.Succeeded)
            {
                refundByHand = true;
                logger.LogError("Cancellation refund of order {OrderId} failed: {Refusal}", order.Id, result.Refusal);
                // Written as failed so it is not asked again and the team sees what is owed.
                if (refundId is null)
                    await repository.AddRefundAsync(new TicketCheckoutRefund
                    {
                        Id = cancelRefundId,
                        OrderId = order.Id,
                        AmountMinor = refundMinor,
                        Reason = Truncate(why),
                        State = RefundState.Failed,
                        FailureReason = Truncate(result.Refusal ?? "Refused"),
                        CreatedAt = now,
                    }, ct);
                refundNote = $"; the {FormatMinor(refundMinor)} {order.Currency} refund failed " +
                    $"({Truncate(result.Refusal ?? "refused")}): the ticketing team must refund it by hand";
                await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, order.Id,
                    $"Cancellation refund{refundNote[1..]}", JobName);
            }
            else if (refundId is { } id)
                refundByHand = (await repository.GetRefundsAsync(order.Id, ct))
                    .Any(r => r.Id == id && r.State is RefundState.Failed or RefundState.ManualRequired);
        }
        refundNote ??= refundMinor <= 0 ? ", nothing to refund"
            : refundByHand ? $"; the ticketing team must refund {FormatMinor(refundMinor)} {order.Currency} by hand"
            : $", {FormatMinor(refundMinor)} {order.Currency} refund asked for";

        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.EventCancelled,
            $"{why}. {live.Count} ticket(s) voided" + (offers.Count > 0 ? $", {offers.Count} taken out of the resale queue" : string.Empty) +
            refundNote, null, ct);
        var eventName = (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(order.BuyerEmail))
        {
            var notice = emails.EventCancelled(order.BuyerUserId, order.BuyerEmail, order.BuyerName, eventName, refundMinor,
                refundByHand, order.Lines.Any(l => l.Kind == OrderLineKind.Donation), order.Culture);
            await email.SendAsync(notice with { DeliveryId = CancellationState.CancelNoticeId(ticketing.Id, order.Id) }, ct);
        }
        order.CancellationEmailedAt = clock.GetCurrentInstant();
        await repository.UpdateOrderAsync(order, ct);
        return true;
    }

    private async Task<CancellationState> LoadCancellationAsync(TicketingSettings ticketing, CancellationToken ct) =>
        new(ticketing,
            await repository.GetOrdersForSettingsAsync(ticketing.Id, ct),
            (await repository.GetTicketsForSettingsAsync(ticketing.Id, ct)).ToLookup(t => t.OrderId),
            (await repository.GetRefundsForSettingsAsync(ticketing.Id, ct)).ToLookup(r => r.OrderId),
            await repository.GetOffersForSettingsAsync(ticketing.Id, ct),
            clock.GetCurrentInstant());

    /// <summary>
    /// One cancelled event as it stands. Before it is cancelled, <see cref="Since"/> is now, so the progress reads as
    /// what a cancellation would do.
    /// </summary>
    private sealed class CancellationState(
        TicketingSettings ticketing,
        IReadOnlyList<TicketCheckoutOrder> orders,
        ILookup<Guid, TicketCheckoutTicket> tickets,
        ILookup<Guid, TicketCheckoutRefund> refunds,
        IReadOnlyList<TicketResaleOffer> offers,
        Instant now)
    {
        public ILookup<Guid, TicketCheckoutTicket> Tickets => tickets;

        public IReadOnlyList<TicketResaleOffer> Offers => offers;

        private Instant Since => ticketing.CancelledAt ?? now;

        private List<TicketCheckoutOrder> Paid => orders.Where(o => o.State == TicketCheckoutOrderState.Paid).ToList();

        public long Refundable(TicketCheckoutOrder order) =>
            new OrderDetail(order, refunds[order.Id].ToList(), [], tickets[order.Id].ToList()).RefundableMinor;

        /// <summary>
        /// The cancellation's own refund of an order has a fixed id, so a refund the team makes meanwhile never stands in for
        /// it, and a repeat finds the one already asked for (a declined one is not tried again).
        /// </summary>
        public static Guid CancelRefundId(Guid settingsId, Guid orderId)
        {
            Span<byte> both = stackalloc byte[32];
            settingsId.TryWriteBytes(both[..16]);
            orderId.TryWriteBytes(both[16..]);
            return new Guid(SHA256.HashData(both)[..16]);
        }

        /// <summary>The cancellation email's delivery id: a repeat after a stop before the order was marked emailed reuses it.</summary>
        public static Guid CancelNoticeId(Guid settingsId, Guid orderId) =>
            new(SHA256.HashData(CancelRefundId(settingsId, orderId).ToByteArray())[..16]);

        public TicketCheckoutRefund? CancelRefund(TicketCheckoutOrder order) =>
            refunds[order.Id].FirstOrDefault(r => r.Id == CancelRefundId(ticketing.Id, order.Id));

        public bool HasCancelRefund(TicketCheckoutOrder order) => CancelRefund(order) is not null;

        /// <summary>
        /// Paid orders not yet emailed (donation only orders too), with a ticket still working, or with money still owed
        /// that the cancellation has not asked for.
        /// </summary>
        public IReadOnlyList<TicketCheckoutOrder> ToDo => Paid
            .Where(o => o.CancellationEmailedAt is null || tickets[o.Id].Any(t => t.IsHeld) || (Refundable(o) > 0 && !HasCancelRefund(o)))
            .OrderBy(o => o.PaidAt)
            .ToList();

        public CancellationProgress Progress
        {
            get
            {
                var paid = Paid;
                var since = paid.SelectMany(o => refunds[o.Id]).Where(r => r.CreatedAt >= Since).ToList();
                return new CancellationProgress(
                    ticketing.CancelledAt, ticketing.CancelledByUserId, ticketing.CancelReason,
                    PaidOrders: paid.Count,
                    ToDo: ToDo.Count,
                    LiveTickets: paid.Sum(o => tickets[o.Id].Count(t => t.IsHeld)),
                    TicketMoneyMinor: paid.Sum(o => o.TicketTotalMinor),
                    ChargedBackMinor: paid.Sum(o => o.ChargedBackMinor),
                    RefundedMinor: paid.Sum(o => Math.Max(o.RefundedMinor,
                        refunds[o.Id].Where(r => r.State == RefundState.Succeeded).Sum(r => r.AmountMinor))),
                    InFlightMinor: paid.Sum(o => refunds[o.Id].Where(r => r.State is RefundState.Pending or RefundState.ManualRequired).Sum(r => r.AmountMinor)),
                    StillRefundableMinor: paid.Sum(Refundable),
                    DonationsMinor: paid.Sum(o => o.TotalMinor - o.TicketTotalMinor),
                    RefundsSince: since.GroupBy(r => r.State)
                        .Select(g => new RefundStateRow(g.Key, g.Count(), g.Sum(r => r.AmountMinor))).ToList());
            }
        }
    }
}
