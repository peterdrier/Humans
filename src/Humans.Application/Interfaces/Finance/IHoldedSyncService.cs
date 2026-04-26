using Humans.Application.DTOs.Finance;

namespace Humans.Application.Interfaces.Finance;

/// <summary>
/// Orchestrates the Holded read-side sync: pulls all purchase docs from
/// <see cref="IHoldedClient"/>, runs match resolution against the budget
/// model, upserts via <see cref="Humans.Application.Interfaces.Repositories.IHoldedRepository"/>,
/// and maintains the singleton <c>HoldedSyncState</c> (Idle / Running / Error).
/// </summary>
public interface IHoldedSyncService
{
    /// <summary>Pulls all purchase docs, matches, upserts. Updates HoldedSyncState.</summary>
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>Manually assigns a doc to a category and pushes the corrected tag back to Holded (best-effort).</summary>
    Task<ReassignOutcome> ReassignAsync(string holdedDocId, Guid budgetCategoryId, Guid actorUserId, CancellationToken ct = default);
}

/// <summary>
/// Result of a manual reassignment. <see cref="LocalMatchSaved"/> is the
/// authoritative outcome — the local DB write is the source of truth.
/// <see cref="TagPushedToHolded"/> reflects best-effort write-back; on failure
/// <see cref="Warning"/> contains a human-readable message for the UI.
/// </summary>
public sealed record ReassignOutcome(bool LocalMatchSaved, bool TagPushedToHolded, string? Warning);
