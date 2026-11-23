using Humans.Base.Interfaces;
using Humans.Gate.Domain;
using NodaTime;

namespace Humans.Gate.Services;

internal interface IGateService : IApplicationService
{
    /// <summary>Write-free; re-evaluated against the server clock on every call.</summary>
    Task<GateScanResult> EvaluateAsync(string barcode, CancellationToken ct = default);

    /// <summary>
    /// Re-evaluates server-side, so client flags never override a STOP. The controller must take
    /// <paramref name="scannedByUserId"/> from the authenticated principal, never the request body.
    /// </summary>
    Task<GateDecisionResult> RecordDecisionAsync(GateDecisionInput input, Guid scannedByUserId, CancellationToken ct = default);

    /// <summary>Unordered; the controller sorts for display.</summary>
    Task<GateLeaderboard> GetLeaderboardAsync(Instant since, CancellationToken ct = default);

    /// <summary>
    /// The current event's tickets, Ticket Tailor and Humans-issued, whose ticket name contains <paramref name="query"/>
    /// or whose holder's burner or legal name matches it; never email. Void and resold tickets are left out. Empty under
    /// two characters, at most 20 rows, unordered.
    /// </summary>
    Task<IReadOnlyList<GateNameMatch>> FindTicketsByNameAsync(string? query, CancellationToken ct = default);

    /// <summary>The kiosk's undo of the barcode's admit, authorized by the shared supervisor PIN like an override.</summary>
    Task<GateUndoResult> UndoAdmitAtGateAsync(string barcode, string? supervisorPin, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The report's undo of one admit, with the admin's stated reason. Authorized at the controller.</summary>
    Task<GateUndoResult> UndoAdmitAsync(Guid scanId, string? reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Arrivals at the active event, per event-local day; null with no active event. Rows are unordered.</summary>
    Task<GateArrivalsReport?> GetArrivalsReportAsync(CancellationToken ct = default);

    Task<GateSettingsDto> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Authorized at the controller.</summary>
    Task SaveSettingsAsync(GateSettingsDto settings, CancellationToken ct = default);

    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>Local admits against the vendor's check-in status as of the last ticket sync.</summary>
    Task<GateVendorBackfillSnapshot> GetVendorCheckInBackfillAsync(CancellationToken ct = default);

    /// <summary>Rechecks pending admits and claims each ticket before enqueueing. Null selects all pending tickets;
    /// an empty or unknown id selects none. Disabled mirroring enqueues nothing.</summary>
    Task<GateVendorBackfillResult> EnqueueVendorCheckInBackfillAsync(string? vendorTicketId, CancellationToken ct = default);

    /// <summary>
    /// De-duplicated signups whose gate shift starts within ±2 hours of now, event-local. Empty with no active event.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetShiftRosterAsync(Guid rosterTeamId, CancellationToken ct = default);

    // Personal device PINs: the claim flow has no reachable page since peterdrier/Humans#1075; deletion is tracked in
    // nobodies-collective/Humans#933. Kiosk overrides verify the shared Gate:SupervisorPin inside RecordDecisionAsync.

    Task<GatePinStatus> GetPinStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Stores <c>AdminEnrolled = false</c>, which <c>AuthorizeOverrideAsync</c> never accepts. Rejects trivial PINs.
    /// </summary>
    Task<GatePinSetResult> SetOwnPinAsync(Guid userId, string pin, CancellationToken ct = default);

    /// <summary>Returns false when the PIN is invalid.</summary>
    Task<bool> AdminSetPinAsync(Guid userId, string pin, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Timing-safe. False when no PIN is set or it doesn't match.</summary>
    Task<bool> VerifyPinAsync(Guid userId, string pin, CancellationToken ct = default);

    /// <summary>
    /// True only for an admin-enrolled PIN that matches, held by a current gate-supervisor role holder. Verify only:
    /// never enrols. No production caller (dormant with the claim flow, nobodies-collective/Humans#933).
    /// </summary>
    Task<bool> AuthorizeOverrideAsync(Guid supervisorUserId, string pin, CancellationToken ct = default);

    /// <summary>They re-enrol on the next claim.</summary>
    Task ClearPinAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);
}


internal sealed record GatePinStatus(bool HasPin);

internal enum GatePinSetResult
{
    Ok,

    /// <summary>Not exactly four digits, or too easily guessed (0000, 1234, repeats).</summary>
    InvalidPin,
}

/// <summary>Write-free result of evaluating a scan, before the agent's ID decision.</summary>
/// <remarks>
/// The card shows only <paramref name="EarliestEntryDate"/>, a date. <paramref name="Today"/> is null when not
/// computed (e.g. an invalid barcode); <paramref name="GeneralEntryDate"/> is null while the cutoff is unconfigured.
/// </remarks>
internal sealed record GateScanResult(
    GatePreCheckOutcome Outcome,
    string Barcode,
    string? GuestName,
    string? TicketTypeName,
    bool IsEarly,
    Guid? TicketAttendeeId,
    Guid? GuestUserId,
    Guid? PreviousAdmitByUserId,
    string? VendorTicketId = null,
    LocalDate? EarliestEntryDate = null,
    LocalDate? Today = null,
    LocalDate? GeneralEntryDate = null)
{
    public Guid? CheckoutTicketId { get; init; }

    /// <summary>An Invalid checkout ticket that is only waiting in the resale queue: the holder can withdraw it.</summary>
    public bool InResaleQueue { get; init; }
}

/// <summary>
/// Carries no scanner identity: the controller supplies it from the authenticated principal, so it can't be forged on the wire. The
/// service verifies <paramref name="SupervisorPin"/> for child waivers and early overrides before recording anything.
/// </summary>
internal sealed record GateDecisionInput(
    string Barcode,
    bool IdConfirmed,
    bool ChildWithAdult,
    string? LaneId,
    Instant? ClientScanAt,
    string? Note,
    bool OverrideEarly = false,
    string? SupervisorPin = null);

internal sealed record GateDecisionResult(
    GateVerdict Verdict,
    string? GuestName,
    bool IsEarly,
    Guid? PreviousAdmitByUserId,
    GateOverrideFailure? OverrideFailure = null,
    int? RetryAfterSeconds = null,
    bool InputTooLong = false);

internal enum GateOverrideFailure
{
    NotConfigured,
    InvalidPin,
    LockedOut,
}

/// <summary><paramref name="Name"/> is the name on the ticket, which the agent checks against the photo ID.</summary>
internal sealed record GateNameMatch(string Barcode, string Name, string? TicketLabel);

internal enum GateUndoOutcome
{
    Undone,

    /// <summary>No standing admit: never admitted, or already undone.</summary>
    NotAdmitted,

    PinRefused,

    ReasonRequired,

    /// <summary>A Ticket Tailor check-in already went (or may still go) to the vendor, which has no undo; the ticket sync would bring it back.</summary>
    VendorCheckedIn,
}

internal sealed record GateUndoResult(
    GateUndoOutcome Outcome, GateOverrideFailure? PinFailure = null, int? RetryAfterSeconds = null);

/// <summary><paramref name="TicketsHeld"/> counts the event's valid tickets, arrived or not.</summary>
internal sealed record GateArrivalsReport(
    string EventName,
    DateTimeZone Zone,
    LocalDate Today,
    int TicketsHeld,
    IReadOnlyList<GateArrivalDay> Days,
    IReadOnlyList<GateArrival> Arrivals)
{
    public int Arrived => Days.Sum(d => d.Admitted);
}

/// <summary>Admit counts leave out undone admits, which <paramref name="Undone"/> counts.</summary>
internal sealed record GateArrivalDay(LocalDate Day, int Admitted, int Early, int Overrides, int Refused, int Undone);

/// <summary>One admit, standing or undone. <paramref name="Day"/> is event-local.</summary>
internal sealed record GateArrival(
    Guid ScanId,
    Instant AdmittedAt,
    LocalDate Day,
    string Name,
    string? TicketLabel,
    GateVerdict Verdict,
    Guid? GuestUserId,
    Instant? UndoneAt,
    Guid? UndoneByUserId,
    string? UndoReason);

internal sealed record GateLeaderboardRow(Guid ScannedByUserId, int Admitted, int Rejected, int Total);

internal sealed record GateLeaderboard(int TotalAdmitted, int TotalScanned, IReadOnlyList<GateLeaderboardRow> Rows);

/// <summary>
/// <paramref name="CutoffConfigured"/> is a read-only projection, ignored on save.
/// </summary>
internal sealed record GateSettingsDto(
    Instant GeneralEntryOpensAt, int MinorAgeThresholdYears, bool CutoffConfigured = false);

/// <summary>
/// <paramref name="VendorTicketId"/> is null for unmirrorable rows (no matched attendee or no vendor ticket id).
/// </summary>
internal sealed record GateVendorBackfillRow(
    string Barcode, string? AttendeeName, Instant AdmittedAt, string? VendorTicketId);

/// <summary>
/// <paramref name="Pending"/> can be mirrored; <paramref name="Unmirrorable"/> can only be fixed on the vendor dashboard.
/// </summary>
internal sealed record GateVendorBackfillSnapshot(
    int AdmitCount,
    int AlreadyCheckedInCount,
    IReadOnlyList<GateVendorBackfillRow> Pending,
    IReadOnlyList<GateVendorBackfillRow> Unmirrorable,
    IReadOnlyList<GateVendorBackfillRow> SentAwaitingSync,
    bool MirrorEnabled);

internal sealed record GateVendorBackfillResult(bool MirrorEnabled, IReadOnlyList<GateVendorBackfillRow> Enqueued);
