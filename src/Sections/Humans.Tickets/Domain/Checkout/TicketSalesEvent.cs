using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// How Humans sells tickets for one event (a Settings event): which checkout backend takes the money, whether sales
/// are open, and the price rule. Several can run at once, one per event, so each backend can be tried side by side.
/// </summary>
internal sealed class TicketSalesEvent
{
    public Guid Id { get; init; }

    /// <summary>The Settings event; unique.</summary>
    public Guid EventSettingsId { get; set; }

    /// <summary>Resolved against <c>ITicketCheckoutBackend.Key</c>.</summary>
    public string BackendKey { get; set; } = string.Empty;

    /// <summary>The event's id inside the backend, passed through on every checkout.</summary>
    public string? BackendEventRef { get; set; }

    public TicketSalesMode Mode { get; set; }

    /// <summary>No checkout opens at or after this instant, whatever the mode.</summary>
    public Instant? SalesCloseAt { get; set; }

    /// <summary>Paid tickets plus tickets held by unexpired checkouts never exceed this.</summary>
    public int Capacity { get; set; }

    /// <summary>A fraction: 0.10 for 10%. Donations carry none.</summary>
    public decimal TicketVatRate { get; set; }

    /// <summary>Ordered by <see cref="TicketSalesWave.EndsOn"/>; the last wave's price holds until sales close.</summary>
    public List<TicketSalesWave> Waves { get; set; } = [];

    /// <summary>Totals of the VIP buttons: the ticket at the current wave price, the remainder a donation.</summary>
    public List<long> SupporterTotalsMinor { get; set; } = [];

    public List<TicketPriceRule> PriceRules { get; set; } = [];

    /// <summary>Returned tickets sell (ahead of fresh stock) once this many tickets are sold; null opens resale at once.</summary>
    public int? ResaleOpensAtSold { get; set; }

    /// <summary>How long an invitation to buy one specific returned ticket stays valid.</summary>
    public int DirectedResaleDays { get; set; } = 3;

    /// <summary>The team whose required legal documents are the ticket rules every buyer accepts; null for none.</summary>
    public Guid? TermsTeamId { get; set; }

    /// <summary>Price of a child ticket attached to an adult's; null when children are not sold.</summary>
    public long? ChildPriceMinor { get; set; }

    /// <summary>Shown to the buyer: a child ticket is for someone under this age on the event's first day.</summary>
    public int? ChildMaxAge { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}

internal enum TicketSalesMode
{
    Closed = 0,
    Open = 1,
    /// <summary>Fresh sales stopped; only returned tickets sell.</summary>
    ResaleOnly = 2,
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

    /// <summary>The class price for <see cref="TicketPriceRuleKind.ClassPrice"/>, the amount off for a credit.</summary>
    public long AmountMinor { get; set; }

    /// <summary>Shown to the buyer on the price breakdown.</summary>
    public string Label { get; set; } = string.Empty;
}

internal enum TicketPriceRuleKind
{
    /// <summary>Replaces the wave price; with several, the cheapest wins.</summary>
    ClassPrice = 0,
    /// <summary>Taken off whatever the ticket costs; credits stack.</summary>
    Credit = 1,
    /// <summary>
    /// The holder pays the wave that was on sale the day the grant was issued, if cheaper than today's (the low income
    /// pin: an applicant keeps their wave whether or not they get a low income place). The amount is unused.
    /// </summary>
    WavePin = 2,
}
