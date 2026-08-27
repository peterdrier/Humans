using Humans.Base.Interfaces;

namespace Humans.Gdpr.Contracts;

/// <summary>
/// Orchestrates both halves of GDPR subject rights over every registered
/// <see cref="IUserDataContributor"/>: the Article 15 export and the Article 17
/// erasure. Owns no tables — each half is a pure fan-out over the same contributor
/// roster, merged (export) or run in turn (erasure).
/// </summary>
public interface IGdprService : IOrchestrator
{
    /// <summary>
    /// Builds a complete GDPR export document for <paramref name="userId"/> by
    /// calling every registered contributor and merging their slices by section
    /// name.
    /// </summary>
    Task<GdprExport> ExportForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Article 17 counterpart of <see cref="ExportForUserAsync"/>: runs every
    /// contributor's <see cref="IUserDataContributor.EraseForUserAsync"/> for each
    /// supplied id, in turn. <paramref name="userIds"/> is the whole merge chain —
    /// the survivor plus every account previously merged into it — which the caller
    /// resolves and passes in, so this orchestrator keeps no dependency on the Users
    /// merge primitive. The contributor that owns the <c>Account</c> identity runs
    /// last within each id, so sections that still need the human's addresses to
    /// reach an external processor can resolve them before the identity collapses.
    /// Sequential and fail-loud: a contributor that throws aborts the run (the caller
    /// leaves its deletion markers set so the whole cascade retries), matching the
    /// export fan-out.
    /// </summary>
    /// <returns>
    /// The ids it erased (the same set passed in), so the caller can invalidate the
    /// per-id caches without re-deriving the chain.
    /// </returns>
    Task<IReadOnlyList<Guid>> EraseForUsersAsync(IReadOnlyList<Guid> userIds, CancellationToken ct = default);
}
