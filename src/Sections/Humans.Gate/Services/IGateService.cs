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

    Task<GateSettingsDto> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Authorized at the controller.</summary>
    Task SaveSettingsAsync(GateSettingsDto settings, CancellationToken ct = default);

    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>Local admits against the vendor's check-in status as of the last ticket sync.</summary>
    Task<GateVendorBackfillSnapshot> GetVendorCheckInBackfillAsync(CancellationToken ct = default);

    /// <summary>
    /// De-duplicated signups whose gate shift starts within ±2 hours of now, event-local. Empty with no active event.
    /// </summary>
    Task<IReadOnlyList<GateRosterEntry>> GetShiftRosterAsync(Guid rosterTeamId, CancellationToken ct = default);

    // Personal device PINs: the claim flow has no reachable page since peterdrier/Humans#1075; deletion is tracked in
    // nobodies-collective/Humans#933. Kiosk overrides use the shared Gate:SupervisorPin at the controller.

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

internal sealed record GateRosterEntry(Guid UserId, string DisplayName);

internal sealed record GatePinStatus(bool HasPin, bool IsSupervisor);

internal enum GatePinSetResult
{
    Ok,

    /// <summary>Not exactly four digits, or too easily guessed (0000, 1234, repeats).</summary>
    InvalidPin,
}

/// <summary>Write-free result of evaluating a scan, before the agent's ID decision.</summary>
/// <remarks>
/// <paramref name="EarlyEntrySource"/> stays server-side for privacy; the card shows only
/// <paramref name="EarliestEntryDate"/>, a date. <paramref name="Today"/> is null when not computed (e.g. an invalid
/// barcode); <paramref name="GeneralEntryDate"/> is null while the cutoff is unconfigured.
/// </remarks>
internal sealed record GateScanResult(
    GatePreCheckOutcome Outcome,
    string Barcode,
    string? GuestName,
    string? TicketTypeName,
    bool IsEarly,
    string? EarlyEntrySource,
    Guid? TicketAttendeeId,
    Guid? GuestUserId,
    Instant? PreviousAdmitAt,
    Guid? PreviousAdmitByUserId,
    string? VendorTicketId = null,
    LocalDate? EarliestEntryDate = null,
    LocalDate? Today = null,
    LocalDate? GeneralEntryDate = null);

/// <summary>
/// Carries no scanner identity: the controller supplies it from the session, so it can't be forged on the wire. The
/// controller sets <paramref name="OverrideByUserId"/> only after verifying the supervisor PIN, so the service treats
/// non-null as proof.
/// </summary>
internal sealed record GateDecisionInput(
    string Barcode,
    bool IdConfirmed,
    bool ChildWithAdult,
    string? LaneId,
    Instant? ClientScanAt,
    string? Note,
    Guid? OverrideByUserId = null);

internal sealed record GateDecisionResult(
    GateVerdict Verdict,
    string? GuestName,
    string? TicketTypeName,
    bool IsEarly,
    Instant? PreviousAdmitAt,
    Guid? PreviousAdmitByUserId,
    string? VendorTicketId = null);

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
    IReadOnlyList<GateVendorBackfillRow> Unmirrorable);
