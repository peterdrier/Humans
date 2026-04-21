using Humans.Domain.Entities;

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
public interface IConsentRepository
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
    /// Returns true if the user has an existing consent record for the given
    /// document version.
    /// </summary>
    Task<bool> ExistsForUserAndVersionAsync(
        Guid userId, Guid documentVersionId, CancellationToken ct = default);

    /// <summary>
    /// Loads a consent record for the given user and document version, or
    /// null if none exists. Read-only (AsNoTracking).
    /// </summary>
    Task<ConsentRecord?> GetByUserAndVersionAsync(
        Guid userId, Guid documentVersionId, CancellationToken ct = default);

    /// <summary>
    /// Returns every consent record for a user, ordered by <c>ConsentedAt</c>
    /// descending. Includes the <see cref="ConsentRecord.DocumentVersion"/>
    /// and nested <see cref="DocumentVersion.LegalDocument"/> navigation
    /// properties so callers can render the document name and version without
    /// further queries. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<ConsentRecord>> GetAllForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of consent records for a user.
    /// </summary>
    Task<int> GetCountForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of <c>DocumentVersionId</c> values that the user has
    /// explicitly consented to.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetExplicitlyConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default);

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
