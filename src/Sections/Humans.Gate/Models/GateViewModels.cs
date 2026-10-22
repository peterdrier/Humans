using Humans.Base.Extensions;
using Humans.Gate.Domain;
using Humans.Gate.Services;

namespace Humans.Gate.Models;

internal enum GateCardKind
{
    Admit,

    Stop,

    IdConfirm,

    Amber,
}

/// <summary>
/// Name, verdict and one reason line only: the Early Entry source, the previous scanner and internal ids never reach
/// the tablet (privacy).
/// </summary>
internal sealed record GateScanCardViewModel(
    GateCardKind Kind,
    string Headline,
    string? GuestName,
    string? Reason,
    bool IsEarly,
    string Barcode,
    bool AllowChildWithAdult,
    bool AllowSupervisorOverride = false)
{
    public static GateScanCardViewModel FromEvaluation(GateScanResult r) => r.Outcome switch
    {
        GatePreCheckOutcome.Invalid =>
            new(GateCardKind.Stop, "Stop", null, "Not a valid ticket for this gate", false, r.Barcode, false),
        GatePreCheckOutcome.Duplicate =>
            new(GateCardKind.Stop, "Stop", r.GuestName, "Already scanned — ticket already used", false, r.Barcode, false),
        GatePreCheckOutcome.CutoffNotConfigured =>
            new(GateCardKind.Amber, "Supervisor", r.GuestName, "Gate cutoff not set — get a supervisor", false, r.Barcode, false),
        // Say which case it is (a later-day EE grant, or none); that informs the override decision.
        GatePreCheckOutcome.TooEarly =>
            new(GateCardKind.Stop, "Stop", r.GuestName, TooEarlyReason(r), false, r.Barcode, false, AllowSupervisorOverride: true),
        // Name search stays the main route, but offer the override too or the card is a dead end at the doors.
        GatePreCheckOutcome.EarlyEntryUnknown =>
            new(GateCardKind.Amber, "Supervisor", r.GuestName, "Early Entry can't be confirmed — search by name", false, r.Barcode, false, AllowSupervisorOverride: true),
        _ => new(GateCardKind.IdConfirm, "Does the ID say", r.GuestName,
            r.IsEarly ? "Early entry OK" : null, r.IsEarly, r.Barcode, AllowChildWithAdult: true),
    };

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

    /// <summary>Date only, never the EE source.</summary>
    private static string TooEarlyReason(GateScanResult r) =>
        r.EarliestEntryDate is { } early
            ? $"Early entry from {early.ToWeekdayDayMonth()}{(r.Today is { } today ? $" — today is {today.ToWeekdayDayMonth()}" : string.Empty)}"
            : r.GeneralEntryDate is { } general
                ? $"No early entry — general entry opens {general.ToWeekdayDayMonth()}"
                : "Too early — no early entry grant";
}

/// <summary><paramref name="CutoffConfigured"/> drives the banner telling staff to have the cutoff set before doors.</summary>
internal sealed record GateIndexViewModel(string DataAsOf, bool CutoffConfigured);

internal sealed record GateSettingsViewModel(
    string GeneralEntryOpensAtUtc,
    // Validation must target the constructor parameter: ASP.NET throws at bind time on a record with property-level
    // validation.
    [param: System.ComponentModel.DataAnnotations.Range(0, 21,
        ErrorMessage = "Minor age threshold must be between 0 and 21.")] int MinorAgeThresholdYears);

internal sealed record GateClaimViewModel(IReadOnlyList<GateRosterMember> Roster);

internal sealed record GateRosterMember(Guid UserId, string DisplayName);

internal enum GatePinMode
{
    Set,

    Verify,
}

internal sealed record GatePinViewModel(Guid UserId, string DisplayName, GatePinMode Mode, string? Error)
{
    /// <summary>From the server-derived status, never a client hint. A self-set claim PIN attributes scans only.</summary>
    public static GatePinViewModel ForClaim(Guid userId, string displayName, GatePinStatus status, string? error = null) =>
        new(userId, displayName, status.HasPin ? GatePinMode.Verify : GatePinMode.Set, error);
}
