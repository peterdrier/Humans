using Humans.Base.Interfaces.Repositories;
using Humans.Gate.Domain;
using NodaTime;

namespace Humans.Gate.Data;

/// <summary>
/// Owns the Gate section's tables (<c>gate_scan_events</c>, <c>gate_settings</c>).
/// The only type permitted to read or write them, per the one-table-one-repository
/// hard rule.
/// </summary>
internal interface IGateRepository : IRepository
{
    /// <summary>The prior admit recorded for this dedupe key (normalized barcode), or null if none — used both for the duplicate pre-check and to show "already admitted at / by" on a duplicate scan.</summary>
    Task<GateScanEvent?> GetAdmitForBarcodeAsync(string admitDedupeKey, CancellationToken ct = default);

    /// <summary>
    /// Append a scan record. For admit verdicts this is the dedupe gate: it returns
    /// <see cref="GateRecordOutcome.DuplicateAdmitRejected"/> if an admit already
    /// exists for the same barcode (explicit pre-check, plus a unique-index catch as
    /// the atomic cross-lane backstop). Reject/unresolved verdicts always record.
    /// </summary>
    Task<GateRecordOutcome> RecordScanAsync(GateScanEvent scan, CancellationToken ct = default);

    /// <summary>All scans at or after <paramref name="since"/>, for audit and leaderboards.</summary>
    Task<IReadOnlyList<GateScanEvent>> GetScansSinceAsync(Instant since, CancellationToken ct = default);

    /// <summary>Every scan involving a user — as the guest admitted (<c>GuestUserId</c>) or as the staffer who scanned (<c>ScannedByUserId</c>) — for the GDPR export.</summary>
    Task<IReadOnlyList<GateScanEvent>> GetScansForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Re-point <c>GuestUserId</c> and <c>ScannedByUserId</c> from <paramref name="fromUserId"/> to <paramref name="toUserId"/> on account merge. Idempotent.</summary>
    Task ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);

    /// <summary>Delete scan rows older than <paramref name="cutoff"/> (retention purge). Returns the number removed.</summary>
    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>The singleton gate settings, or a default instance if none has been saved yet.</summary>
    Task<GateSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Insert-or-update the singleton gate settings (Id is forced to 1).</summary>
    Task SaveSettingsAsync(GateSettings settings, CancellationToken ct = default);

    /// <summary>The staffer's personal-PIN row, or null if they have not set one.</summary>
    Task<GateStaffPin?> GetStaffPinAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Insert-or-update a staffer's personal-PIN row (hash already computed by the service).</summary>
    Task UpsertStaffPinAsync(GateStaffPin pin, CancellationToken ct = default);

    /// <summary>Remove a staffer's personal-PIN row (admin reset / merge). No-op if none exists.</summary>
    Task DeleteStaffPinAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Result of <see cref="IGateRepository.RecordScanAsync"/>.</summary>
internal enum GateRecordOutcome
{
    /// <summary>The scan was appended.</summary>
    Recorded,

    /// <summary>An admit already existed for this barcode; the duplicate admit was not recorded.</summary>
    DuplicateAdmitRejected,
}
