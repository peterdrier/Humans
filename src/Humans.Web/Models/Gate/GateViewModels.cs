using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gate;
using Humans.Domain.Enums;

namespace Humans.Web.Models.Gate;

/// <summary>The visual state of a gate result card — drives colour and layout.</summary>
public enum GateCardKind
{
    /// <summary>Green — admitted.</summary>
    Admit,

    /// <summary>Red — turned away.</summary>
    Stop,

    /// <summary>Blue-grey question — valid ticket, agent must confirm the photo ID (Yes/No).</summary>
    IdConfirm,

    /// <summary>Amber — can't decide automatically; supervisor / name search.</summary>
    Amber,
}

/// <summary>
/// Gate result card view model. Deliberately projected to <b>name + verdict + one
/// reason line only</b> — Early-Entry source, the previous scanner's identity, and
/// internal GUIDs stay server-side and never reach the tablet (privacy review I2).
/// Built from the service DTOs by the factory methods below so the controller
/// holds no mapping logic.
/// </summary>
public sealed record GateScanCardViewModel(
    GateCardKind Kind,
    string Headline,
    string? GuestName,
    string? Reason,
    bool IsEarly,
    string Barcode,
    bool AllowChildWithAdult,
    bool AllowSupervisorOverride = false)
{
    /// <summary>Map a live (pre-ID) evaluation to a card.</summary>
    public static GateScanCardViewModel FromEvaluation(GateScanResult r) => r.Outcome switch
    {
        GatePreCheckOutcome.Invalid =>
            new(GateCardKind.Stop, "Stop", null, "Not a valid ticket for this gate", false, r.Barcode, false),
        GatePreCheckOutcome.Duplicate =>
            new(GateCardKind.Stop, "Stop", r.GuestName, "Already scanned — ticket already used", false, r.Barcode, false),
        GatePreCheckOutcome.CutoffNotConfigured =>
            new(GateCardKind.Amber, "Supervisor", r.GuestName, "Gate cutoff not set — get a supervisor", false, r.Barcode, false),
        // Too early: tell the supervisor WHICH case it is (holds a later-day EE grant, vs no grant at
        // all), and offer a supervisor override either way (the reason is what informs the decision).
        GatePreCheckOutcome.TooEarly =>
            new(GateCardKind.Stop, "Stop", r.GuestName, TooEarlyReason(r), false, r.Barcode, false, AllowSupervisorOverride: true),
        GatePreCheckOutcome.EarlyEntryUnknown =>
            new(GateCardKind.Amber, "Supervisor", r.GuestName, "Early Entry can't be confirmed — search by name", false, r.Barcode, false),
        // NeedsIdCheck / NeedsIdCheckEarly → ask the agent to confirm the ID.
        _ => new(GateCardKind.IdConfirm, "Does the ID say", r.GuestName,
            r.IsEarly ? "Early entry OK" : null, r.IsEarly, r.Barcode, AllowChildWithAdult: true),
    };

    /// <summary>Map a recorded decision to a final card.</summary>
    public static GateScanCardViewModel FromDecision(GateDecisionResult d, string barcode) => d.Verdict switch
    {
        GateVerdict.AdmittedEarlyOverride =>
            new(GateCardKind.Admit, "Admit", d.GuestName,
                "Early — supervisor override · give wristband", IsEarly: true, barcode, false),
        GateVerdict.Admitted or GateVerdict.AdmittedEarly or GateVerdict.AdmittedChildWithAdult =>
            new(GateCardKind.Admit, "Admit", d.GuestName,
                "Give wristband · check photo ID matches", d.IsEarly, barcode, false),
        GateVerdict.RejectedDuplicate =>
            new(GateCardKind.Stop, "Stop", d.GuestName, "Already scanned — ticket already used", false, barcode, false),
        GateVerdict.RejectedTooEarly =>
            new(GateCardKind.Stop, "Stop", d.GuestName, "Too early — no Early Entry ticket", false, barcode, false),
        GateVerdict.RejectedNameMismatch =>
            new(GateCardKind.Stop, "Stop", d.GuestName, "ID name does not match — turn away", false, barcode, false),
        GateVerdict.Unresolved =>
            new(GateCardKind.Amber, "Supervisor", d.GuestName, "Needs a supervisor decision", false, barcode, false),
        _ => new(GateCardKind.Stop, "Stop", d.GuestName, "Not a valid ticket for this gate", false, barcode, false),
    };

    /// <summary>
    /// The precise too-early reason. A holder with a <em>later-day</em> Early Entry grant
    /// (e.g. a Friday EE ticket scanned on Wednesday) reads differently from one with no grant
    /// at all — the supervisor needs that distinction to decide on an override. Date only.
    /// </summary>
    private static string TooEarlyReason(GateScanResult r) =>
        r.EarliestEntryDate is { } early
            ? $"Early entry from {early.ToWeekdayDayMonth()}{(r.Today is { } today ? $" — today is {today.ToWeekdayDayMonth()}" : string.Empty)}"
            : r.GeneralEntryDate is { } general
                ? $"No early entry — general entry opens {general.ToWeekdayDayMonth()}"
                : "Too early — no early entry grant";
}

/// <summary>The scan page shell. <paramref name="CutoffConfigured"/> drives a loud
/// warning banner: while the general-entry cutoff is unset, every scan fails safe to
/// AMBER, so the terminal tells staff to have an admin set it before doors.</summary>
public sealed record GateIndexViewModel(
    string ScannerName, bool DataStale, string DataAsOf, bool CutoffConfigured,
    IReadOnlyList<GateSupervisorOption> Supervisors);

/// <summary>One enrolled supervisor offered as a tap-pick on the override panel (kiosk has no free-text search).</summary>
public sealed record GateSupervisorOption(Guid UserId, string DisplayName);

/// <summary>Gate settings admin form.</summary>
public sealed record GateSettingsViewModel(
    string GeneralEntryOpensAtUtc,
    // Validation metadata must target the record's constructor PARAMETER, not the generated
    // property — ASP.NET throws at POST-bind time for a record with property-level validation.
    [param: System.ComponentModel.DataAnnotations.Range(0, 21,
        ErrorMessage = "Minor age threshold must be between 0 and 21.")] int MinorAgeThresholdYears);

/// <summary>One leaderboard row (name resolved in the view via <c>&lt;vc:human&gt;</c>).</summary>
public sealed record GateLeaderboardRowViewModel(Guid ScannedByUserId, int Admitted, int Rejected, int Total);

/// <summary>
/// The "Who is scanning?" claim screen. <see cref="Roster"/> is a one-tap quick-pick of
/// people signed up for the gate shift roster (empty unless <c>Gate:RosterTeamId</c> is
/// configured and that team has signups); the search box below it always finds anyone
/// — including helpers who aren't rostered.
/// </summary>
public sealed record GateClaimViewModel(IReadOnlyList<GateRosterMember> Roster);

/// <summary>One pre-filled roster pick on the claim screen.</summary>
public sealed record GateRosterMember(Guid UserId, string DisplayName);

/// <summary>What the PIN keypad is for.</summary>
public enum GatePinMode
{
    /// <summary>First use — choose a new 4-digit PIN.</summary>
    Set,

    /// <summary>Returning — enter the existing PIN to take over the scanner.</summary>
    Verify,
}

/// <summary>The full-screen PIN keypad shown after a name is picked on the claim screen.</summary>
public sealed record GatePinViewModel(Guid UserId, string DisplayName, GatePinMode Mode, string? Error)
{
    /// <summary>
    /// Resolve the keypad mode from the server-derived PIN status (never a client-supplied hint):
    /// has a PIN → Verify; otherwise Set. Everyone — supervisors included — may self-enrol a claim
    /// PIN; it attributes scans only, never authorizes an override (that stays admin-enrolled).
    /// </summary>
    public static GatePinViewModel ForClaim(Guid userId, string displayName, GatePinStatus status, string? error = null) =>
        new(userId, displayName, status.HasPin ? GatePinMode.Verify : GatePinMode.Set, error);
}
