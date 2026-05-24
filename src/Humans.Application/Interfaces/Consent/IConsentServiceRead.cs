using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Consent;

/// <summary>
/// Cross-section read surface for the Consent section. External sections inject
/// this interface; only Consent projections (RequiredConsentRow, ConsentReviewDetail)
/// and value-type reads, no EF entities and no writes/cache hooks.
/// See memory/architecture/section-read-write-split.md.
/// </summary>
[SurfaceBudget(6)]
public interface IConsentServiceRead
{
    /// <summary>
    /// Gets the set of document version IDs that a user has explicitly consented to.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets a map of user ID → consented document version IDs for a batch of users.
    /// Every input user ID appears in the result (with an empty set if no consents).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns one row per active+required document scoped to <paramref name="teamId"/>:
    /// the current document version, its display title, the review URL, and whether
    /// the user has already signed it. Pure read — no side effects. Used by the
    /// onboarding widget Consents step to render the per-document sign list.
    /// </summary>
    Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Returns the human-friendly names of legal documents the user has not yet
    /// consented to (i.e., documents with a current version the user is missing).
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the count of consent records for a user.
    /// </summary>
    Task<int> GetConsentRecordCountAsync(
        Guid userId, CancellationToken ct = default);

    Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default);
}
