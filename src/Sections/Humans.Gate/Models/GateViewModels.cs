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
    bool AllowSupervisorOverride = false,
    bool AllowUndo = false)
{
    public static GateScanCardViewModel FromEvaluation(GateScanResult r) => r.Outcome switch
    {
        GatePreCheckOutcome.Invalid when r.InResaleQueue =>
            new(GateCardKind.Stop, "Stop", r.GuestName,
                "Offered for resale — the holder must withdraw it in Humans, then scan again", false, r.Barcode, false),
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

    public static GateScanCardViewModel FromDecision(GateDecisionResult d, string barcode, bool childWithAdult = false) => d switch
    {
        { OverrideFailure: { } failure } =>
            new(GateCardKind.Amber, "Supervisor", null, failure switch
            {
                GateOverrideFailure.NotConfigured => "Override PIN not configured — an admin must set it before overrides work",
                GateOverrideFailure.LockedOut => $"Too many PIN attempts — wait {d.RetryAfterSeconds}s",
                _ => "PIN not accepted — try again",
            }, false, barcode, AllowChildWithAdult: false, AllowSupervisorOverride: !childWithAdult),
        _ => FromVerdict(d, barcode),
    };

    private static GateScanCardViewModel FromVerdict(GateDecisionResult d, string barcode) => d.Verdict switch
    {
        GateVerdict.AdmittedEarlyOverride =>
            new(GateCardKind.Admit, "Admit", d.GuestName,
                "Early — supervisor override · give wristband", IsEarly: true, barcode, false, AllowUndo: true),
        GateVerdict.Admitted or GateVerdict.AdmittedEarly or GateVerdict.AdmittedChildWithAdult =>
            new(GateCardKind.Admit, "Admit", d.GuestName,
                "Give wristband · check photo ID matches", d.IsEarly, barcode, false, AllowUndo: true),
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

    /// <summary>A refused PIN keeps the undo button so the supervisor can try again.</summary>
    public static GateScanCardViewModel FromUndo(GateUndoResult r, string barcode) => r.Outcome switch
    {
        GateUndoOutcome.Undone =>
            new(GateCardKind.Amber, "Undone", null,
                "Check-in undone — take the wristband back · the ticket can be scanned again", false, barcode, false),
        GateUndoOutcome.PinRefused =>
            new(GateCardKind.Amber, "Supervisor", null, r.PinFailure switch
            {
                GateOverrideFailure.NotConfigured => "Override PIN not configured — an admin must set it before undo works",
                GateOverrideFailure.LockedOut => $"Too many PIN attempts — wait {r.RetryAfterSeconds}s",
                _ => "PIN not accepted — try again",
            }, false, barcode, false, AllowUndo: true),
        GateUndoOutcome.VendorCheckedIn =>
            new(GateCardKind.Amber, "Supervisor", null,
                "Can't undo here — Ticket Tailor has this check-in too · get the gate lead", false, barcode, false),
        _ => new(GateCardKind.Amber, "Supervisor", null, "Nothing to undo — this ticket is not checked in", false, barcode, false),
    };

    /// <summary>Date only, never the EE source.</summary>
    private static string TooEarlyReason(GateScanResult r) =>
        r.EarliestEntryDate is { } early
            ? $"Early entry from {early.ToWeekdayDayMonth()}{(r.Today is { } today ? $" — today is {today.ToWeekdayDayMonth()}" : string.Empty)}"
            : r.GeneralEntryDate is { } general
                ? $"No early entry — general entry opens {general.ToWeekdayDayMonth()}"
                : "Too early — no early entry grant";
}

/// <summary>Ticket names only: the burner or legal name a row matched on never reaches the kiosk.</summary>
internal sealed record GateFindResultsViewModel(string Query, IReadOnlyList<GateNameMatch> Matches);

/// <summary><paramref name="CutoffConfigured"/> drives the banner telling staff to have the cutoff set before doors.</summary>
internal sealed record GateIndexViewModel(string DataAsOf, bool CutoffConfigured);

internal sealed record GateSettingsViewModel(string GeneralEntryOpensAtUtc);

/// <summary><paramref name="Days"/> newest first; <paramref name="DayArrivals"/> are the shown day's admits, newest first.</summary>
internal sealed record GateReportViewModel(
    GateArrivalsReport Report,
    NodaTime.LocalDate ShownDay,
    IReadOnlyList<GateArrivalDay> Days,
    IReadOnlyList<GateArrival> DayArrivals,
    bool CanUndo);
