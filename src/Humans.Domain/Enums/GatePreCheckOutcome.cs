namespace Humans.Domain.Enums;

/// <summary>
/// What a gate scan resolves to <em>before</em> the manual photo-ID check.
/// Computed server-side, fresh on every scan, by the pure
/// <c>GateAdmissionRules.Evaluate</c>. The three STOP outcomes end the scan; the
/// two NeedsIdCheck outcomes hand off to the agent's Yes/No ID decision; the two
/// AMBER outcomes escalate to a supervisor.
/// </summary>
public enum GatePreCheckOutcome
{
    /// <summary>Not found for the current event, or void/refunded — STOP (red).</summary>
    Invalid,

    /// <summary>Already admitted (local) or already checked in at the vendor — STOP (red), duplicate.</summary>
    Duplicate,

    /// <summary>The general-entry cutoff has not been configured, so Early-Entry gating cannot be decided — AMBER, supervisor. Fails safe: never a silent admit while the cutoff is unset (an admin must set it before doors).</summary>
    CutoffNotConfigured,

    /// <summary>Before the general-entry cutoff with no Early Entry covering today — STOP (red).</summary>
    TooEarly,

    /// <summary>Before the cutoff and Early Entry cannot be confirmed (ticket not matched to a Human) — AMBER, supervisor + name search. Never silently treated as TooEarly.</summary>
    EarlyEntryUnknown,

    /// <summary>Valid and entitled (general entry is open) — proceed to the photo-ID Yes/No check.</summary>
    NeedsIdCheck,

    /// <summary>Valid and an Early Entry grant covers today (before the cutoff) — proceed to the photo-ID Yes/No check, admitting early on Yes.</summary>
    NeedsIdCheckEarly,
}
