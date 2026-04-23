using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Consent;

public record ConsentSubmitResult(bool Success, string? DocumentName = null, string? ErrorKey = null);

public interface IConsentService
{
    Task<(List<(Team Team, List<(DocumentVersion Version, ConsentRecord? Consent)> Documents)> Groups,
          List<ConsentRecord> History)>
        GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);

    Task<(DocumentVersion? Version, ConsentRecord? ExistingConsent, string? UserFullName)>
        GetConsentReviewDetailAsync(Guid documentVersionId, Guid userId, CancellationToken ct = default);

    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);

    /// <summary>
    /// Gets all consent records for a user, ordered by most recent first,
    /// with DocumentVersion and LegalDocument navigation properties loaded.
    /// </summary>
    Task<IReadOnlyList<ConsentRecord>> GetUserConsentRecordsAsync(
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
}
