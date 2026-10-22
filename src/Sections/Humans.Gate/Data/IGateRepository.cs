using Humans.Base.Interfaces.Repositories;
using Humans.Gate.Domain;
using NodaTime;

namespace Humans.Gate.Data;

internal interface IGateRepository : IRepository
{
    /// <summary>Null when none. Feeds the duplicate pre-check and the "already admitted at / by" card.</summary>
    Task<GateScanEvent?> GetAdmitForBarcodeAsync(string admitDedupeKey, CancellationToken ct = default);

    /// <summary>
    /// For admits this is the dedupe gate: an existing admit for the barcode returns
    /// <see cref="GateRecordOutcome.DuplicateAdmitRejected"/>. Other verdicts always record.
    /// </summary>
    Task<GateRecordOutcome> RecordScanAsync(GateScanEvent scan, CancellationToken ct = default);

    Task<IReadOnlyList<GateScanEvent>> GetScansSinceAsync(Instant since, CancellationToken ct = default);

    /// <summary>As guest or as the staffer who scanned.</summary>
    Task<IReadOnlyList<GateScanEvent>> GetScansForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Also carries the merged-away staff PIN to the survivor.</summary>
    Task ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);

    /// <summary>
    /// Clears <c>GuestUserId</c> and <c>OverrideByUserId</c> and deletes the staff PIN. <c>ScannedByUserId</c> is
    /// non-nullable and stays; see the erasure declaration.
    /// </summary>
    Task<int> EraseUserFromScansAsync(Guid userId, CancellationToken ct = default);

    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>A default instance when none has been saved yet.</summary>
    Task<GateSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Id is forced to 1.</summary>
    Task SaveSettingsAsync(GateSettings settings, CancellationToken ct = default);

    Task<GateStaffPin?> GetStaffPinAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The hash is already computed by the service.</summary>
    Task UpsertStaffPinAsync(GateStaffPin pin, CancellationToken ct = default);

    /// <summary>No-op when none exists.</summary>
    Task DeleteStaffPinAsync(Guid userId, CancellationToken ct = default);
}

internal enum GateRecordOutcome
{
    Recorded,

    DuplicateAdmitRejected,
}
