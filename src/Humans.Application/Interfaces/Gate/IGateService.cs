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
}

/// <summary>Write-free result of evaluating a scan, before the agent's ID decision.</summary>
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
    string? VendorTicketId = null);

/// <summary>
/// The agent's decision for a scan, recorded server-side after re-evaluation.
/// Deliberately carries no scanner identity — that is supplied separately by the
/// controller from the authenticated session so it cannot be forged on the wire.
/// </summary>
public sealed record GateDecisionInput(
    string Barcode,
    bool IdConfirmed,
    bool ChildWithAdult,
    string? LaneId,
    Instant? ClientScanAt,
    string? Note);

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
