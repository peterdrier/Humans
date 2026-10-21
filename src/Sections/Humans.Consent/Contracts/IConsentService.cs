using Humans.Base.Interfaces;

namespace Humans.Consent.Contracts;

/// <summary>
/// Public contract of the Consent section. External sections inject
/// this interface; only Consent projections (RequiredConsentRow, ConsentReviewDetail, ConsentReviewInfo)
/// and value-type reads, no EF entities and no writes/cache hooks.
/// </summary>
public interface IConsentService : IApplicationService
{
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

    Task<int> GetConsentRecordCountAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Returns null when the version or its canonical Spanish content is unavailable.</summary>
    Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The member's Consent Coordinator review; <see cref="ConsentReviewInfo.None"/> when nothing
    /// is recorded. A merged-away id reads its survivor's review.
    /// </summary>
    Task<ConsentReviewInfo> GetConsentReviewAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every member with a recorded review, keyed by user id. A member missing from the
    /// map reads as <see cref="ConsentReviewInfo.None"/>.</summary>
    Task<IReadOnlyDictionary<Guid, ConsentReviewInfo>> GetConsentReviewsAsync(CancellationToken ct = default);
}
