using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// How Humans sells tickets for one event (a Settings event): which ticketing platform takes the money, whether sales
/// are open, and the price rule. Several can run at once, one per event, so each platform can be tried side by side.
/// </summary>
internal sealed class TicketingSettings
{
    public Guid Id { get; init; }

    /// <summary>The Settings event; unique.</summary>
    public Guid EventSettingsId { get; set; }

    /// <summary>Resolved against <c>ITicketingPlatform.Key</c>.</summary>
    public string PlatformKey { get; set; } = string.Empty;

    /// <summary>The event's id inside the platform, passed through on every platform order.</summary>
    public string? PlatformEventRef { get; set; }

    public TicketSalesMode Mode { get; set; }

    /// <summary>No order opens at or after this instant, whatever the mode.</summary>
    public Instant? SalesCloseAt { get; set; }

    /// <summary>Paid tickets plus tickets held by unexpired orders never exceed this.</summary>
    public int Capacity { get; set; }

    /// <summary>A fraction: 0.10 for 10% (the ticketing settings form shows a percentage). Donations carry none.</summary>
    public decimal TicketVatRate { get; set; }

    /// <summary>Ordered by <see cref="TicketSalesWave.EndsOn"/>; the last wave's price holds until sales close.</summary>
    public List<TicketSalesWave> Waves { get; set; } = [];

    /// <summary>Totals of the VIP buttons: the ticket at the current wave price, the remainder a donation.</summary>
    public List<long> VipTotalsMinor { get; set; } = [];

    public List<TicketPriceRule> PriceRules { get; set; } = [];

    /// <summary>
    /// When the ticketing team opened resale by hand (on the resale queue page); null while paused, and tickets in resale
    /// wait. Not part of the ticketing settings form, so saving the settings keeps it.
    /// </summary>
    public Instant? ResaleOpenedAt { get; set; }

    /// <summary>Which schemes this event sells; null (settings saved before the switches) means the defaults.</summary>
    public TicketingSwitches? Switches { get; set; }

    public TicketingSwitches EffectiveSwitches => Switches ?? new();

    /// <summary>Whether tickets in resale sell now. Resale only mode sells nothing else, so it opens resale too.</summary>
    public bool IsResaleSelling(Instant now) =>
        !IsClosed(now) && EffectiveSwitches.Resale != ResaleScheme.Off
        && Mode is (TicketSalesMode.Open or TicketSalesMode.ResaleOnly)
        && (ResaleOpenedAt is not null || Mode == TicketSalesMode.ResaleOnly);

    /// <summary>The close instant is exclusive for new sales and resale actions.</summary>
    public bool IsClosed(Instant now) => Mode == TicketSalesMode.Closed || (SalesCloseAt is { } close && now >= close);

    /// <summary>
    /// The folder in the legal repo holding the ticket rules every buyer accepts, one markdown file per language; null
    /// for none. Every commit changing the folder is a new version.
    /// </summary>
    public string? TermsFolder { get; set; }

    /// <summary>
    /// The child tickets sold on an adult's ticket, youngest first by <see cref="TicketChildType.UnderAge"/>; empty when
    /// children are not sold. The buyer picks one per child.
    /// </summary>
    public List<TicketChildType> ChildTypes { get; set; } = [];

    /// <summary>
    /// What a free donation may be given for, as the buyer sees it. Null or empty (settings saved before purposes
    /// existed): one general donation, no choice. A donation with no purpose (the VIP remainder too) is general.
    /// </summary>
    public List<string>? DonationPurposes { get; set; }

    /// <summary>
    /// When a super admin cancelled the event; never undone. Sales stay closed, and the reconcile job voids every ticket
    /// and refunds every paid order what its tickets cost.
    /// </summary>
    public Instant? CancelledAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public string? CancelReason { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}

internal enum TicketSalesMode
{
    Closed = 0,
    Open = 1,
    /// <summary>Fresh sales stopped; only resale offers sell.</summary>
    ResaleOnly = 2,
    /// <summary>
    /// Early sales before general sale opens: only holders of an <see cref="TicketPriceRuleKind.EarlyAccess"/> grant
    /// buy, fresh stock only, no tickets for others and no resale. Also how a few people make a test purchase.
    /// </summary>
    InvitedOnly = 3,
}

/// <summary>
/// The schemes an organisation may or may not run. The defaults are the Nobodies scheme; another organisation turns
/// off what it does not sell. Enforced in the checkout service, not only hidden on the buy page.
/// Warning: stored as JSON, and a member missing from a stored row reads as the CLR default (false, Off), not as the
/// initializer below. A switch added later must default to false (name it so) or be nullable with null as the
/// default. Never rename a <see cref="ResaleScheme"/> member: an unknown string fails every ticketing settings read.
/// </summary>
internal sealed class TicketingSwitches
{
    public ResaleScheme Resale { get; set; } = ResaleScheme.QueueAndDirected;

    /// <summary>A buyer may add other people already in Humans to the order, each an adult ticket at the public price.</summary>
    public bool PartnerTickets { get; set; } = true;

    /// <summary>Donation lines at checkout: the VIP buttons and the free donation.</summary>
    public bool Donations { get; set; } = true;

    /// <summary>Checkout asks a buyer with no Marketing choice on record to join the mailing list.</summary>
    public bool MailingListOptIn { get; set; } = true;

    /// <summary>
    /// A holder may ask for a free companion ticket, which the ticketing team approves. Null (settings saved before this
    /// switch) means on; read <see cref="CompanionTicketsOn"/>.
    /// </summary>
    public bool? CompanionTickets { get; set; }

    public bool CompanionTicketsOn => CompanionTickets ?? true;
}

/// <summary>A child ticket for someone under <see cref="UnderAge"/> on the event's first day.</summary>
internal sealed class TicketChildType
{
    public string Name { get; set; } = string.Empty;

    public int UnderAge { get; set; }

    public long PriceMinor { get; set; }
}

/// <summary>How a holder who cannot come gets their money back.</summary>
internal enum ResaleScheme
{
    /// <summary>Holders cannot put a ticket up for resale; the ticketing team refunds by hand when it agrees to.</summary>
    Off = 0,

    /// <summary>A ticket in resale waits in the public queue and sells, oldest first, before fresh stock.</summary>
    Queue = 1,

    /// <summary>The queue, plus a holder may send people they choose a link to buy their ticket.</summary>
    QueueAndDirected = 2,
}

/// <summary>A wave is on sale through <see cref="EndsOn"/>, inclusive, in the event's time zone.</summary>
internal sealed class TicketSalesWave
{
    public string Name { get; set; } = string.Empty;

    public LocalDate EndsOn { get; set; }

    public long PriceMinor { get; set; }
}

/// <summary>What an active grant in <see cref="GrantProgramId"/> does to the price.</summary>
internal sealed class TicketPriceRule
{
    public Guid GrantProgramId { get; set; }

    public TicketPriceRuleKind Kind { get; set; }

    /// <summary>The class price for <see cref="TicketPriceRuleKind.ClassPrice"/>, the amount off for a discount.</summary>
    public long AmountMinor { get; set; }

    /// <summary>Shown to the buyer on the price breakdown.</summary>
    public string Label { get; set; } = string.Empty;
}

internal enum TicketPriceRuleKind
{
    /// <summary>Replaces the wave price; with several, the cheapest wins.</summary>
    ClassPrice = 0,
    /// <summary>Taken off whatever the ticket costs; discounts stack.</summary>
    Discount = 1,
    /// <summary>
    /// The holder pays the wave that was on sale the day the grant was issued, if cheaper than today's (the low income
    /// pin: an applicant keeps their wave whether or not they get a low income place). The amount is unused.
    /// </summary>
    WavePin = 2,
    /// <summary>
    /// The holder may buy while sales are <see cref="TicketSalesMode.InvitedOnly"/>, with the amount (zero for none)
    /// taken off while they do. Does nothing once sales open to everyone.
    /// </summary>
    EarlyAccess = 3,
}
