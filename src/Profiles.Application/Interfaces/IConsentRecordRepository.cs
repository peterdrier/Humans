using Profiles.Domain.Entities;

namespace Profiles.Application.Interfaces;

/// <summary>
/// Repository for consent records. This is a read-only, append-only repository.
/// Consent records cannot be updated or deleted to maintain GDPR audit trail.
/// </summary>
public interface IConsentRecordRepository
{
    /// <summary>
    /// Gets all consent records for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All consent records for the user.</returns>
    Task<IReadOnlyList<ConsentRecord>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent consent record for a user and document version.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="documentVersionId">The document version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The consent record if found.</returns>
    Task<ConsentRecord?> GetByUserAndVersionAsync(Guid userId, Guid documentVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has valid consent for a document version.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="documentVersionId">The document version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the user has consented to this version.</returns>
    Task<bool> HasConsentAsync(Guid userId, Guid documentVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all document version IDs that a user has consented to.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Set of document version IDs with valid consent.</returns>
    Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new consent record. This is the only write operation allowed.
    /// </summary>
    /// <param name="consentRecord">The consent record to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(ConsentRecord consentRecord, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets users who have not consented to a specific document version.
    /// </summary>
    /// <param name="documentVersionId">The document version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user IDs without consent.</returns>
    Task<IReadOnlyList<Guid>> GetUsersWithoutConsentAsync(Guid documentVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets consented version IDs for multiple users in a single query.
    /// </summary>
    /// <param name="userIds">The user IDs to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping user ID to set of consented version IDs.</returns>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentedVersionIdsByUsersAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);
}
