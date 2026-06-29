using AwesomeAssertions;
using Humans.Application.Services.Gate;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Tests.Services.Gate;

/// <summary>
/// Exhaustive coverage of <see cref="GateAdmissionRules.Evaluate"/> — the pure
/// admission precedence at the heart of the gate. Each test pins one branch or
/// boundary so a precedence regression (e.g. a duplicate leaking past the void
/// check, or a too-early stop swallowing an unknown-Early-Entry case) fails
/// loudly.
/// </summary>
public class GateAdmissionRulesTests
{
    // Noon Mon 6 Jul 2026 Europe/Madrid (CEST, UTC+2) == 10:00 UTC.
    private static readonly Instant Cutoff = Instant.FromUtc(2026, 7, 6, 10, 0, 0);
    private static readonly LocalDate Today = new(2026, 7, 5);

    /// <summary>A valid ticket scanned after the cutoff, matched, no Early Entry —
    /// the baseline "should reach the ID check" case. Tests mutate one field.</summary>
    private static GateScanContext Valid(
        bool found = true,
        bool isVoid = false,
        bool admittedLocally = false,
        bool checkedInAtVendor = false,
        Instant? now = null,
        bool matched = true,
        LocalDate? earliest = null,
        bool cutoffSet = true) => new(
            Found: found,
            IsVoid: isVoid,
            AlreadyAdmittedLocally: admittedLocally,
            CheckedInAtVendor: checkedInAtVendor,
            Now: now ?? Cutoff.Plus(Duration.FromHours(1)),
            GeneralEntryOpensAt: cutoffSet ? Cutoff : null,
            MatchedToHuman: matched,
            EarliestEntryDate: earliest,
            Today: Today);

    [HumansFact]
    public void NotFound_IsInvalid() =>
        GateAdmissionRules.Evaluate(Valid(found: false)).Should().Be(GatePreCheckOutcome.Invalid);

    [HumansFact]
    public void Void_IsInvalid() =>
        GateAdmissionRules.Evaluate(Valid(isVoid: true)).Should().Be(GatePreCheckOutcome.Invalid);

    [HumansFact]
    public void Invalid_BeatsDuplicate()
    {
        // Void + already-admitted: invalid wins (precedence #1 before dedupe).
        GateAdmissionRules.Evaluate(Valid(isVoid: true, admittedLocally: true))
            .Should().Be(GatePreCheckOutcome.Invalid);
    }

    [HumansFact]
    public void AlreadyAdmittedLocally_IsDuplicate() =>
        GateAdmissionRules.Evaluate(Valid(admittedLocally: true)).Should().Be(GatePreCheckOutcome.Duplicate);

    [HumansFact]
    public void CheckedInAtVendor_IsDuplicate() =>
        GateAdmissionRules.Evaluate(Valid(checkedInAtVendor: true)).Should().Be(GatePreCheckOutcome.Duplicate);

    [HumansFact]
    public void Duplicate_BeatsEarlyEntryChecks()
    {
        // Before the cutoff AND already admitted: duplicate wins over any EE logic.
        GateAdmissionRules.Evaluate(Valid(admittedLocally: true, now: Cutoff.Minus(Duration.FromHours(1)), matched: false))
            .Should().Be(GatePreCheckOutcome.Duplicate);
    }

    [HumansFact]
    public void CutoffNotConfigured_IsAmber_NotSilentAdmit()
    {
        // Unset cutoff must fail safe to AMBER, never be treated as "general entry open".
        GateAdmissionRules.Evaluate(Valid(cutoffSet: false))
            .Should().Be(GatePreCheckOutcome.CutoffNotConfigured);
    }

    [HumansFact]
    public void CutoffNotConfigured_StillYieldsToInvalidAndDuplicate()
    {
        // Precedence: a void or already-used ticket still STOPs even with no cutoff set.
        GateAdmissionRules.Evaluate(Valid(isVoid: true, cutoffSet: false))
            .Should().Be(GatePreCheckOutcome.Invalid);
        GateAdmissionRules.Evaluate(Valid(admittedLocally: true, cutoffSet: false))
            .Should().Be(GatePreCheckOutcome.Duplicate);
    }

    [HumansFact]
    public void GeneralEntryOpen_AtExactCutoff_NeedsIdCheck()
    {
        // Boundary: now == cutoff is "open" (inclusive).
        GateAdmissionRules.Evaluate(Valid(now: Cutoff)).Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansFact]
    public void GeneralEntryOpen_UnmatchedTicket_StillNeedsIdCheck()
    {
        // After the cutoff, Early Entry is moot — an unmatched valid ticket is admissible.
        GateAdmissionRules.Evaluate(Valid(now: Cutoff.Plus(Duration.FromMinutes(1)), matched: false))
            .Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansFact]
    public void BeforeCutoff_Unmatched_IsEarlyEntryUnknown()
    {
        // Cannot confirm EE for an unmatched ticket → AMBER, never a silent too-early stop.
        GateAdmissionRules.Evaluate(Valid(now: Cutoff.Minus(Duration.FromHours(2)), matched: false))
            .Should().Be(GatePreCheckOutcome.EarlyEntryUnknown);
    }

    [HumansFact]
    public void BeforeCutoff_Matched_NoEarlyEntry_IsTooEarly()
    {
        GateAdmissionRules.Evaluate(Valid(now: Cutoff.Minus(Duration.FromHours(2)), earliest: null))
            .Should().Be(GatePreCheckOutcome.TooEarly);
    }

    [HumansFact]
    public void BeforeCutoff_EarlyEntryToday_NeedsIdCheckEarly()
    {
        GateAdmissionRules.Evaluate(Valid(now: Cutoff.Minus(Duration.FromHours(2)), earliest: Today))
            .Should().Be(GatePreCheckOutcome.NeedsIdCheckEarly);
    }

    [HumansFact]
    public void BeforeCutoff_EarlyEntryEarlierThanToday_NeedsIdCheckEarly()
    {
        GateAdmissionRules.Evaluate(Valid(now: Cutoff.Minus(Duration.FromHours(2)), earliest: Today.PlusDays(-1)))
            .Should().Be(GatePreCheckOutcome.NeedsIdCheckEarly);
    }

    [HumansFact]
    public void BeforeCutoff_EarlyEntryNotYetValid_IsTooEarly()
    {
        // Holder has EE, but their earliest date is still in the future relative to today.
        GateAdmissionRules.Evaluate(Valid(now: Cutoff.Minus(Duration.FromHours(2)), earliest: Today.PlusDays(1)))
            .Should().Be(GatePreCheckOutcome.TooEarly);
    }
}
