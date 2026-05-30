using Humans.Application.Interfaces.Legal;
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
public record RequiredConsentRow(Guid DocumentVersionId, string Title, bool Signed)
{
    /// <summary>
    /// Shapes the onboarding-widget Consents rows from the active+required
    /// document set and the user's consented-version ids: one row per document
    /// at its current version (latest <c>EffectiveFrom &lt;= now</c>), unsigned
    /// first so outstanding work bubbles to the top of the widget.
    /// </summary>
    /// <remarks>
    /// Single source of truth for both the repo-backed <c>ConsentService</c>
    /// read and the <c>CachingConsentService</c> decorator, which compose the
    /// two inputs differently (repo vs cache) but must produce the identical
    /// row shape and ordering.
    /// </remarks>
    public static IReadOnlyList<RequiredConsentRow> BuildOrdered(
        IReadOnlyList<ActiveRequiredLegalDocumentSnapshot> documents,
        IReadOnlySet<Guid> consentedVersionIds,
        Instant now)
    {
        var rows = new List<RequiredConsentRow>(documents.Count);
        foreach (var doc in documents)
        {
            var currentVersion = doc.Versions
                .Where(v => v.EffectiveFrom <= now)
                .MaxBy(v => v.EffectiveFrom);

            if (currentVersion is null)
                continue;

            rows.Add(new RequiredConsentRow(
                DocumentVersionId: currentVersion.Id,
                Title: doc.Name,
                Signed: consentedVersionIds.Contains(currentVersion.Id)));
        }

        return rows
            .OrderBy(r => r.Signed)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ToList();
    }
}

public interface IConsentService : IConsentServiceRead, IApplicationService
{
    Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);

    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);
}
