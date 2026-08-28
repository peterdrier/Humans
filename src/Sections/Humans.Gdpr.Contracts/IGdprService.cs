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
    /// contributor's <see cref="IUserDataContributor.EraseForUserAsync"/> for a single
    /// account id, in turn, with the contributor that owns the <c>Account</c> identity
    /// last — so sections that still need the human's addresses to reach an external
    /// processor can resolve them before the identity collapses. Ordering is derived
    /// from the declarations, not a pinned type list. Takes only an id, so this
    /// orchestrator keeps no dependency on the Users merge primitive or its caches.
    /// Sequential and fail-loud: a contributor that throws aborts the run.
    /// <para>
    /// The caller loops this over the whole merge chain (archived ids first, survivor
    /// last) and invalidates each id's caches as it completes, so a failure partway
    /// through the chain still leaves the already-erased ids' caches dropped.
    /// </para>
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct = default);
}
