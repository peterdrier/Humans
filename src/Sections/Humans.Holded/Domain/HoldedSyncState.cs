using NodaTime;

namespace Humans.Holded.Domain;

internal sealed class HoldedSyncState
{
    public HoldedSyncKind Kind { get; init; }

    /// <summary>The treasury account for <see cref="HoldedSyncKind.BankMovements"/>: each feed has its own state.
    /// Empty for every other kind.</summary>
    public string Scope { get; init; } = "";

    public Instant? LastSyncAt { get; set; }
    public HoldedSyncStatus SyncStatus { get; set; } = HoldedSyncStatus.Idle;

    /// <summary>Residual reconciliation mismatches, or the failure that stopped the sweep.
    /// A standing known mismatch is reportable state, not an error to throw on.</summary>
    public string? LastError { get; set; }

    public Instant? StatusChangedAt { get; set; }
    public int LastCount { get; set; }
}
