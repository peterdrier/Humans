using Humans.Base.Interfaces;
using Humans.Consent.Domain;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>Sole writer for <c>consent_legal_documents</c> and <c>consent_document_versions</c>; the admin writes are on the same instance.</summary>
internal interface ILegalDocumentSyncManagement : IApplicationService
{
    /// <summary>The latest effective version per required, active document.</summary>
    Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>Across all teams.</summary>
    Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default);

    Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns changed documents; scheduled callers collect failures without stopping successful work.</summary>
    Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default, ICollection<Exception>? failures = null);

    Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>With team names stitched and version snapshots populated; null selects all teams.</summary>
    Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid>? teamIds, CancellationToken cancellationToken = default);
}

internal sealed record ActiveRequiredLegalDocumentSnapshot(
    Guid Id,
    string Name,
    Guid TeamId,
    string TeamName,
    Instant LastSyncedAt,
    IReadOnlyList<LegalDocumentVersionSnapshot> Versions)
{
    public LegalDocumentVersionSnapshot? CurrentVersionAt(Instant now) =>
        Versions.Where(version => version.EffectiveFrom <= now).MaxBy(version => version.EffectiveFrom);
}

internal sealed record LegalDocumentVersionSnapshot(
    Guid Id,
    string LegalDocumentName,
    int LegalDocumentGracePeriodDays,
    string VersionNumber,
    IReadOnlyDictionary<string, string> Content,
    Instant EffectiveFrom,
    bool RequiresReConsent,
    string? ChangesSummary);

internal sealed record RequiredDocumentVersionSnapshot(
    Guid Id,
    string LegalDocumentName,
    int LegalDocumentGracePeriodDays,
    Instant EffectiveFrom);
