using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// What one ticket costs one person: the wave price on their pricing date, replaced by the cheapest class price their
/// grants give, minus every credit they hold, never below zero. Donations are separate lines and never enter here.
/// </summary>
internal sealed record TicketPrice(
    TicketSalesWave Wave,
    TicketPriceRule? ClassPrice,
    IReadOnlyList<TicketPriceRule> Credits,
    long AmountMinor)
{
    /// <summary>
    /// Null when the event has no waves. <paramref name="pinnedOn"/> is the earliest issue date of the person's
    /// <see cref="TicketPriceRuleKind.WavePin"/> grants; the wave on sale then applies when it is cheaper.
    /// <paramref name="early"/> adds the largest <see cref="TicketPriceRuleKind.EarlyAccess"/> amount as a credit.
    /// </summary>
    public static TicketPrice? For(
        TicketSalesEvent salesEvent, LocalDate pricingDate, IReadOnlyCollection<Guid> activeProgramIds, LocalDate? pinnedOn = null,
        bool early = false)
    {
        var wave = WaveOn(salesEvent, pricingDate);
        if (wave is null)
            return null;
        if (pinnedOn is { } pin && pin < pricingDate && WaveOn(salesEvent, pin) is { } pinned && pinned.PriceMinor < wave.PriceMinor)
            wave = pinned;

        var rules = salesEvent.PriceRules.Where(r => activeProgramIds.Contains(r.GrantProgramId)).ToList();
        var classPrice = rules
            .Where(r => r.Kind == TicketPriceRuleKind.ClassPrice)
            .OrderBy(r => r.AmountMinor)
            .FirstOrDefault();
        var credits = rules.Where(r => r.Kind == TicketPriceRuleKind.Credit).ToList();
        // One early discount per person: an asociado who is also a colaborador gets the larger, not both.
        if (early && rules.Where(r => r.Kind == TicketPriceRuleKind.EarlyAccess && r.AmountMinor > 0)
                .MaxBy(r => r.AmountMinor) is { } earlyCredit)
            credits.Add(earlyCredit);

        var amount = (classPrice?.AmountMinor ?? wave.PriceMinor) - credits.Sum(c => c.AmountMinor);
        return new TicketPrice(wave, classPrice, credits, Math.Max(0, amount));
    }

    /// <summary>The public price: no grants, today's wave.</summary>
    public static TicketPrice? Public(TicketSalesEvent salesEvent, LocalDate today) => For(salesEvent, today, []);

    public static TicketSalesWave? WaveOn(TicketSalesEvent salesEvent, LocalDate date)
    {
        var waves = salesEvent.Waves.OrderBy(w => w.EndsOn).ToList();
        return waves.FirstOrDefault(w => w.EndsOn >= date) ?? waves.LastOrDefault();
    }
}
