using Humans.Gate.Domain;
using NodaTime;

namespace Humans.Gate.Services;

/// <summary>
/// Every time is a UTC instant except <paramref name="Today"/>, the event-zone date (the unit Early Entry is granted
/// in). A null <paramref name="GeneralEntryOpensAt"/> means the cutoff isn't configured: the scan fails safe to AMBER.
/// </summary>
internal sealed record GateScanContext(
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
/// The pre-ID-check outcome only; the caller records the durable <see cref="GateVerdict"/> after the agent acts.
/// </summary>
internal static class GateAdmissionRules
{
    /// <summary>
    /// First match wins:
    /// <list type="number">
    /// <item>not found / void → <see cref="GatePreCheckOutcome.Invalid"/></item>
    /// <item>admitted locally / checked in at vendor → <see cref="GatePreCheckOutcome.Duplicate"/></item>
    /// <item>cutoff not configured → <see cref="GatePreCheckOutcome.CutoffNotConfigured"/></item>
    /// <item>now ≥ cutoff → <see cref="GatePreCheckOutcome.NeedsIdCheck"/></item>
    /// <item>before cutoff, unmatched → <see cref="GatePreCheckOutcome.EarlyEntryUnknown"/></item>
    /// <item>before cutoff, Early Entry covers today → <see cref="GatePreCheckOutcome.NeedsIdCheckEarly"/></item>
    /// <item>otherwise → <see cref="GatePreCheckOutcome.TooEarly"/></item>
    /// </list>
    /// </summary>
    public static GatePreCheckOutcome Evaluate(GateScanContext ctx)
    {
        if (!ctx.Found || ctx.IsVoid)
            return GatePreCheckOutcome.Invalid;

        if (ctx.AlreadyAdmittedLocally || ctx.CheckedInAtVendor)
            return GatePreCheckOutcome.Duplicate;

        // Cutoff unset: fail safe to AMBER, never treat it as "general entry already open".
        if (ctx.GeneralEntryOpensAt is not { } generalEntryOpensAt)
            return GatePreCheckOutcome.CutoffNotConfigured;

        if (ctx.Now >= generalEntryOpensAt)
            return GatePreCheckOutcome.NeedsIdCheck;

        // Unmatched means Early Entry can't be evaluated: escalate rather than turn a legitimate holder away.
        if (!ctx.MatchedToHuman)
            return GatePreCheckOutcome.EarlyEntryUnknown;

        if (ctx.EarliestEntryDate is { } earliest && earliest <= ctx.Today)
            return GatePreCheckOutcome.NeedsIdCheckEarly;

        return GatePreCheckOutcome.TooEarly;
    }
}
