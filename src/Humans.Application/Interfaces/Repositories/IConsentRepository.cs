using Humans.Domain.Entities;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Legal &amp; Consent section's <c>consent_records</c>
/// table. The only non-test file that writes to <c>DbContext.ConsentRecords</c>
/// after the ConsentService migration lands (issue #547).
/// </summary>
/// <remarks>
/// <para>
/// <c>consent_records</c> is append-only per design-rules §12 — only
/// <see cref="AddAsync"/> is exposed; there are no <c>UpdateAsync</c>,
/// <c>DeleteAsync</c>, or <c>RemoveAsync</c> methods. Database triggers
/// additionally reject any UPDATE or DELETE at the storage layer, so even
/// direct DbContext mutations from other code paths will fail at runtime.
/// New state = new row.
/// </para>
/// <para>
/// Uses <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// so the repository can be registered as Singleton while
/// <c>HumansDbContext</c> remains Scoped.
/// </para>
/// </remarks>
[Section("Consent")]
public interface IConsentRepository : IRepository
{
    // ==========================================================================
    // Writes — append-only
    // ==========================================================================

    /// <summary>
    /// Appends a new consent record. Persisted immediately (auto-saved).
    /// This is the only write method; there are no updates or deletes.
    /// </summary>
    Task AddAsync(ConsentRecord record, CancellationToken ct = default);

    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <summary>
    /// Returns true if any of the given user ids has an existing consent
    /// record for the given document version. Used by the service-layer
    /// chain-follow read path (the id set is <c>{userId ∪ merged-source-ids}</c>)
    /// so a fold-target's existing-consent check transparently includes
    /// consents that stayed attributed to merged-source tombstones.
    /// </summary>
    Task<bool> ExistsForUserIdsAndVersionAsync(
        IReadOnlyCollection<Guid> userIds, Guid documentVersionId, CancellationToken ct = default);

    /// <summary>
    /// Loads a consent record for the given user ids and document version, or
    /// null if none exists. Used by the service-layer chain-follow read path
    /// (the id set is <c>{userId ∪ merged-source-ids}</c>) so a fold-target's
    /// review detail transparently surfaces a consent record that stayed
    /// attributed to a merged-source tombstone. Returns the most recent
    /// matching record (ordered by <c>ConsentedAt</c> descending) when
    /// multiple ids consented to the same version. Read-only (AsNoTracking).
    /// </summary>
    Task<ConsentRecord?> GetByUserIdsAndVersionAsync(
        IReadOnlyCollection<Guid> userIds, Guid documentVersionId, CancellationToken ct = default);

    /// <summary>
    /// Returns every consent record for the given user ids, ordered by
    /// <c>ConsentedAt</c> descending. Includes the
    /// <see cref="ConsentRecord.DocumentVersion"/> and nested
    /// <see cref="DocumentVersion.LegalDocument"/> navigation properties so
    /// callers can render the document name and version without further
    /// queries. Used by the service-layer chain-follow read path (the id set
    /// is <c>{userId ∪ merged-source-ids}</c>) so a fold-target's history
    /// transparently includes records that stayed attributed to merged-source
    /// tombstones. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<ConsentRecord>> GetAllForUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of consent records across the given user ids. Used by
    /// the service-layer chain-follow read path (the id set is
    /// <c>{userId ∪ merged-source-ids}</c>) so a fold-target's count
    /// transparently includes records that stayed attributed to merged-source
    /// tombstones.
    /// </summary>
    Task<int> GetCountForUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the flat union of <c>DocumentVersionId</c> values that the
    /// given user ids have explicitly consented to. Used by the service-layer
    /// chain-follow read path (the id set is <c>{userId ∪ merged-source-ids}</c>)
    /// so a fold-target's consented-versions set transparently includes
    /// versions that were explicitly consented to by merged-source tombstones.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetExplicitlyConsentedVersionIdsForUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// For each input user id, returns the set of <c>DocumentVersionId</c>
    /// values that user has explicitly consented to. Every input user id
    /// appears in the result (empty set when the user has no explicit
    /// consents).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetExplicitlyConsentedVersionIdsForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>(UserId, DocumentVersionId)</c> pairs for the given set
    /// of users and document versions. Used by the re-consent notification
    /// job to find which users are missing consent to updated versions.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, Guid DocumentVersionId)>> GetPairsForUsersAndVersionsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Guid> documentVersionIds,
        CancellationToken ct = default);
}
