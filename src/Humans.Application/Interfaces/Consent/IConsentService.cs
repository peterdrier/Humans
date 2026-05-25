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

public interface IConsentService : IConsentServiceRead, IApplicationService
{
    Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);

    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);

    /// <summary>
    /// Gets all consent records for a user, ordered by most recent first.
    /// </summary>
    [Obsolete("No production callers as of 2026-05; retained only for ChainFollowReadTests. Delete-sweep candidate.")]
    Task<IReadOnlyList<ConsentRecordSnapshot>> GetUserConsentRecordsAsync(
        Guid userId, CancellationToken ct = default);
}
