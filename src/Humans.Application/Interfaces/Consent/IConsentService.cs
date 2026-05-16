using NodaTime;

namespace Humans.Application.Interfaces.Consent;

public record ConsentSubmitResult(bool Success, string? DocumentName = null, string? ErrorKey = null);

public record ConsentDashboard(
    IReadOnlyList<ConsentDashboardTeamGroup> Groups,
    IReadOnlyList<ConsentDashboardHistoryItem> History);

public record ConsentDashboardTeamGroup(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<ConsentDashboardDocument> Documents);

public record ConsentDashboardDocument(
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    Instant EffectiveFrom,
    bool HasConsented,
    Instant? ConsentedAt,
    string? ChangesSummary,
    Instant? LastUpdated);

public record ConsentDashboardHistoryItem(
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    Instant ConsentedAt);

public record ConsentReviewDetail(
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    IReadOnlyDictionary<string, string> Content,
    Instant EffectiveFrom,
    string? ChangesSummary,
    bool HasAlreadyConsented,
    Instant? ConsentedAt,
    string? UserFullName);

/// <summary>
/// One row in the onboarding-widget Consents step: a single required document
/// (current version) for the user, with whether they have already signed it.
/// </summary>
public record RequiredConsentRow(Guid DocumentVersionId, string Title, bool Signed);

public record ConsentRecordSnapshot(
    Guid UserId,
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    Instant ConsentedAt);

public interface IConsentService : IApplicationService
{
    Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);

    Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default);

    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);

    /// <summary>
    /// Gets all consent records for a user, ordered by most recent first.
    /// </summary>
    Task<IReadOnlyList<ConsentRecordSnapshot>> GetUserConsentRecordsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the count of consent records for a user.
    /// </summary>
    Task<int> GetConsentRecordCountAsync(
        Guid userId, CancellationToken ct = default);

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
    /// Returns the human-friendly names of legal documents the user has not yet
    /// consented to (i.e., documents with a current version the user is missing).
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns one row per active+required document scoped to <paramref name="teamId"/>:
    /// the current document version, its display title, the review URL, and whether
    /// the user has already signed it. Pure read — no side effects. Used by the
    /// onboarding widget Consents step to render the per-document sign list.
    /// </summary>
    Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default);
}
