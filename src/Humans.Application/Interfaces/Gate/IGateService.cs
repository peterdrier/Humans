using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Gate;

/// <summary>
/// The Gate (admissions) section service: evaluates a scanned ticket against the
/// admission rules, records the agent's decision as an append-only
/// <c>gate_scan_events</c> row (the dedupe authority and audit trail), and reads
/// back leaderboard/settings. Owns the Gate tables via <c>IGateRepository</c> and
/// reads Tickets / EarlyEntry / BurnSettings through their cross-section
/// interfaces. Not consumed cross-section, so no read/write split is needed.
/// </summary>
public interface IGateService : IApplicationService
{
    /// <summary>Live, write-free verdict for a freshly scanned barcode. Re-evaluated against the server clock every call.</summary>
    Task<GateScanResult> EvaluateAsync(string barcode, CancellationToken ct = default);

    /// <summary>
    /// Record the agent's decision after the photo-ID step. The scan is
    /// re-evaluated server-side (the client-supplied flags never override a STOP),
    /// then the durable verdict is written. Returns the final verdict for display.
    /// <paramref name="scannedByUserId"/> is the authenticated gate staffer, which
    /// the controller MUST take from the session — never from the request body —
    /// so the audit attribution cannot be forged.
    /// </summary>
    Task<GateDecisionResult> RecordDecisionAsync(GateDecisionInput input, Guid scannedByUserId, CancellationToken ct = default);

    /// <summary>Unordered per-staffer scan tallies since <paramref name="since"/> (controller sorts for display).</summary>
    Task<GateLeaderboard> GetLeaderboardAsync(Instant since, CancellationToken ct = default);

    /// <summary>The current gate settings (general-entry cutoff + minor age threshold).</summary>
    Task<GateSettingsDto> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Persist gate settings (admin only; authorized at the controller).</summary>
    Task SaveSettingsAsync(GateSettingsDto settings, CancellationToken ct = default);

    /// <summary>Retention purge: delete scan rows older than <paramref name="cutoff"/>. Returns the count removed.</summary>
    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>
    /// The de-duplicated volunteers signed up for gate shifts on <paramref name="rosterTeamId"/>
    /// whose shift starts within ±2 hours of now (event-local time) — the people likely working
    /// the gate around shift change. Used to pre-fill the claim screen. Empty when there's no
    /// active event or no shift starting in that window.
    /// </summary>
    Task<IReadOnlyList<GateRosterEntry>> GetShiftRosterAsync(Guid rosterTeamId, CancellationToken ct = default);

    // ── Personal device PINs ─────────────────────────────────────────────────
    // Each gate staffer sets a 4-digit PIN once and reuses it to claim the shared
    // scanner (real attribution) and to authorize supervisor overrides. Hashes only.

    /// <summary>Whether <paramref name="userId"/> has set a PIN, and whether they hold a gate-supervisor role.</summary>
    Task<GatePinStatus> GetPinStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Kiosk self-enrolment: a staffer sets their own PIN. Refuses to enrol anyone holding a
    /// supervisor role (their PIN carries override authority — those are admin-enrolled out of
    /// band, never cold at the anonymous kiosk) and rejects weak/trivial PINs.
    /// </summary>
    Task<GatePinSetResult> SetOwnPinAsync(Guid userId, string pin, CancellationToken ct = default);

    /// <summary>Admin sets/initialises any user's PIN (incl. supervisors), from the gate admin page. <paramref name="actorUserId"/> is the acting admin (audit). Returns false if the PIN is invalid.</summary>
    Task<bool> AdminSetPinAsync(Guid userId, string pin, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Verify a staffer's PIN for claiming the scanner. Timing-safe. False if no PIN set or mismatch.</summary>
    Task<bool> VerifyPinAsync(Guid userId, string pin, CancellationToken ct = default);

    /// <summary>
    /// Authorize a supervisor override: true only if the asserted supervisor is enrolled, the
    /// PIN matches, AND they currently hold a gate-supervisor role — all server-verified in one
    /// call. Verify-only: an un-enrolled or non-supervisor user is rejected, never enrolled.
    /// </summary>
    Task<bool> AuthorizeOverrideAsync(Guid supervisorUserId, string pin, CancellationToken ct = default);

    /// <summary>Clear a user's PIN (admin reset). <paramref name="actorUserId"/> is the acting admin (audit). They re-enrol on next claim.</summary>
    Task ClearPinAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// The user-ids of every gate-supervisor (Admin/Board/TicketAdmin) who has a PIN enrolled —
    /// i.e. the people who can actually authorize an override. Drives the kiosk override picker
    /// (a tap-list, since the locked-down terminal can't reach free-text people search). Names are
    /// resolved by the caller for display; the authority itself is always re-checked by
    /// <see cref="AuthorizeOverrideAsync"/> at submit time.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetEnrolledSupervisorIdsAsync(CancellationToken ct = default);
}

/// <summary>One pre-filled gate-roster pick: a Humans user signed up for a current gate shift.</summary>
public sealed record GateRosterEntry(Guid UserId, string DisplayName);

/// <summary>Whether a staffer has a device PIN set, and whether they hold a gate-supervisor role.</summary>
public sealed record GatePinStatus(bool HasPin, bool IsSupervisor);

/// <summary>Outcome of a kiosk self-enrolment attempt.</summary>
public enum GatePinSetResult
{
    /// <summary>PIN set.</summary>
    Ok,

    /// <summary>The PIN is not exactly four digits or is too easily guessed (e.g. 0000, 1234, repeats).</summary>
    InvalidPin,
}

/// <summary>Write-free result of evaluating a scan, before the agent's ID decision.</summary>
/// <param name="EarliestEntryDate">The earliest event-local date the holder may enter (their Early Entry grant), or null if they hold none. Drives the precise too-early reason. Date only — the EE source stays server-side (privacy I2).</param>
/// <param name="Today">Today's calendar date in the event time zone, for the "today is …" half of the too-early reason. Null when not computed (e.g. invalid barcode).</param>
/// <param name="GeneralEntryDate">The event-local date general entry opens, for the no-Early-Entry too-early reason. Null when the cutoff is unconfigured.</param>
public sealed record GateScanResult(
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
/// The agent's decision for a scan, recorded server-side after re-evaluation.
/// Deliberately carries no scanner identity — that is supplied separately by the
/// controller from the authenticated session so it cannot be forged on the wire.
/// <paramref name="OverrideByUserId"/> is the supervisor who authorized an override
/// (too-early or child-without-ID): the controller sets it ONLY after
/// <see cref="IGateService.AuthorizeOverrideAsync"/> has verified that supervisor's PIN
/// and role, so the service treats a non-null value as proof the override was authorized.
/// </summary>
public sealed record GateDecisionInput(
    string Barcode,
    bool IdConfirmed,
    bool ChildWithAdult,
    string? LaneId,
    Instant? ClientScanAt,
    string? Note,
    Guid? OverrideByUserId = null);

/// <summary>The durable verdict plus the bits needed to render the result screen.</summary>
public sealed record GateDecisionResult(
    GateVerdict Verdict,
    string? GuestName,
    string? TicketTypeName,
    bool IsEarly,
    Instant? PreviousAdmitAt,
    Guid? PreviousAdmitByUserId,
    string? VendorTicketId = null);

/// <summary>One leaderboard row: a staffer's scan tallies.</summary>
public sealed record GateLeaderboardRow(Guid ScannedByUserId, int Admitted, int Rejected, int Total);

/// <summary>Aggregated gate activity for a window.</summary>
public sealed record GateLeaderboard(int TotalAdmitted, int TotalScanned, IReadOnlyList<GateLeaderboardRow> Rows);

/// <summary>
/// Gate configuration DTO (mirrors the singleton settings row).
/// <paramref name="CutoffConfigured"/> is a read-only projection of whether a real
/// general-entry cutoff has been set (it is ignored on save — an admin always
/// writes a concrete cutoff); it lets the terminal warn loudly while unset.
/// </summary>
public sealed record GateSettingsDto(
    Instant GeneralEntryOpensAt, int MinorAgeThresholdYears, bool CutoffConfigured = false);
