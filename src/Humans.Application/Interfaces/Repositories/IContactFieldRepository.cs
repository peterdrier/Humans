using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>contact_fields</c> table.
/// The only non-test file that may write to this DbSet.
/// </summary>
public interface IContactFieldRepository
{
    /// <summary>
    /// Returns all contact fields for a profile, read-only, ordered by
    /// <c>DisplayOrder</c> then <c>CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns contact fields for a profile filtered by allowed visibility
    /// levels, read-only, ordered by <c>DisplayOrder</c> then <c>CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<ContactField>> GetVisibleByProfileIdAsync(
        Guid profileId, IReadOnlyList<ContactFieldVisibility> allowedVisibilities,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all contact fields for a profile as tracked entities for
    /// in-place modification. Used by the batch save flow.
    /// </summary>
    Task<IReadOnlyList<ContactField>> GetByProfileIdTrackedAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Atomic batch write: adds new fields, removes deleted fields, and
    /// persists all changes (including mutations on tracked entities) in
    /// one <c>SaveChangesAsync</c> call.
    /// </summary>
    Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default);
}
