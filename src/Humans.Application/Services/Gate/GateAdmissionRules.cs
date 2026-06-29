using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Gate;

/// <summary>
/// The immutable inputs to a single gate-scan decision. Assembled by the Gate
/// service from cross-section reads (Tickets, EarlyEntry, BurnSettings) and this
/// section's own dedupe table, then handed to <see cref="GateAdmissionRules"/>.
/// All time inputs are UTC <see cref="Instant"/>s except <paramref name="Today"/>,
/// which is the calendar date in the event time zone (the unit Early Entry is
/// granted in).
/// </summary>
/// <param name="Found">A current-event ticket matched this barcode.</param>
/// <param name="IsVoid">The ticket is void / refunded / cancelled (not admissible).</param>
/// <param name="AlreadyAdmittedLocally">This section already recorded an admit for this barcode (instant dedupe, authoritative before the next vendor sync).</param>
/// <param name="CheckedInAtVendor">The vendor already reports this ticket as checked in (secondary dedupe signal from the last sync).</param>
/// <param name="Now">Server clock, the single source of truth for the cutoff comparison.</param>
/// <param name="GeneralEntryOpensAt">The instant general entry opens to all valid tickets; before it, Early Entry is required. <c>null</c> means the cutoff has not been configured yet — the scan fails safe to AMBER rather than silently admitting.</param>
/// <param name="MatchedToHuman">The ticket is matched to a Humans account, so Early Entry can be evaluated.</param>
/// <param name="EarliestEntryDate">The earliest date the matched Human may enter, or null if they hold no Early Entry grant.</param>
/// <param name="Today">Today's calendar date in the event time zone.</param>
public sealed record GateScanContext(
    bool Found,
    bool IsVoid,
    bool AlreadyAdmittedLocally,
    bool CheckedInAtVendor,
    Instant Now,
    Instant? GeneralEntryOpensAt,
    bool MatchedToHuman,
    LocalDate? EarliestEntryDate,
    LocalDate Today);

/// <summary>
/// Pure, side-effect-free gate-admission decision. Single source of the
/// admission precedence so it can be exhaustively unit-tested in isolation and
/// reused by both the live scan endpoint and the pre-doors self-test. Computes
/// the pre-ID-check outcome only — the photo-ID Yes/No and the durable
/// <see cref="GateVerdict"/> are recorded by the caller after the agent acts.
/// </summary>
public static class GateAdmissionRules
{
    /// <summary>
    /// Resolve a scan to its pre-ID-check outcome. Precedence (first match wins):
    /// <list type="number">
    /// <item>not found / void → <see cref="GatePreCheckOutcome.Invalid"/></item>
    /// <item>already admitted locally / already checked in at vendor → <see cref="GatePreCheckOutcome.Duplicate"/></item>
    /// <item>cutoff not configured → <see cref="GatePreCheckOutcome.CutoffNotConfigured"/> (AMBER, fail-safe — never a silent admit)</item>
    /// <item>general entry open (now ≥ cutoff) → <see cref="GatePreCheckOutcome.NeedsIdCheck"/></item>
    /// <item>before cutoff, not matched to a Human → <see cref="GatePreCheckOutcome.EarlyEntryUnknown"/> (AMBER, never a silent too-early stop)</item>
    /// <item>before cutoff, matched, Early Entry covers today → <see cref="GatePreCheckOutcome.NeedsIdCheckEarly"/></item>
    /// <item>before cutoff, matched, no covering Early Entry → <see cref="GatePreCheckOutcome.TooEarly"/></item>
    /// </list>
    /// </summary>
    public static GatePreCheckOutcome Evaluate(GateScanContext ctx)
    {
        if (!ctx.Found || ctx.IsVoid)
            return GatePreCheckOutcome.Invalid;

        if (ctx.AlreadyAdmittedLocally || ctx.CheckedInAtVendor)
            return GatePreCheckOutcome.Duplicate;

        // Cutoff unset: Early-Entry gating is undecidable, so fail safe to AMBER
        // rather than treating "no cutoff" as "general entry already open" (which
        // would silently admit everyone before an admin configures the cutoff).
        if (ctx.GeneralEntryOpensAt is not { } generalEntryOpensAt)
            return GatePreCheckOutcome.CutoffNotConfigured;

        // General entry open: any valid, un-used ticket is admissible; Early Entry is moot.
        if (ctx.Now >= generalEntryOpensAt)
            return GatePreCheckOutcome.NeedsIdCheck;

        // Before the cutoff: Early Entry is required. If we cannot evaluate it
        // (ticket not matched to a Human), escalate rather than wrongly turning
        // a legitimate Early Entry holder away.
        if (!ctx.MatchedToHuman)
            return GatePreCheckOutcome.EarlyEntryUnknown;

        if (ctx.EarliestEntryDate is { } earliest && earliest <= ctx.Today)
            return GatePreCheckOutcome.NeedsIdCheckEarly;

        return GatePreCheckOutcome.TooEarly;
    }
}
