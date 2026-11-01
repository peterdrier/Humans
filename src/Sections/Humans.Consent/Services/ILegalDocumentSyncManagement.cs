using Humans.Consent.Contracts;
using Humans.Consent.Domain;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>Sole writer for <c>legal_documents</c> and <c>document_versions</c>; the admin writes are on the same instance.</summary>
internal interface ILegalDocumentSyncManagement : ILegalDocumentSyncService
{
    /// <summary>Across all teams.</summary>
    Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default);

    Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the documents that changed.</summary>
    Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a summary message if updated, or null if already up to date.</summary>
    Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(CancellationToken cancellationToken = default);

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

internal sealed record LegalDocumentSnapshot(
    Guid Id,
    string Name,
    Guid TeamId,
    int GracePeriodDays,
    string? GitHubFolderPath,
    string CurrentCommitSha,
    bool IsRequired,
    bool IsActive,
    Instant CreatedAt,
    Instant LastSyncedAt,
    IReadOnlyList<LegalDocumentVersionSnapshot> Versions);

internal sealed record LegalDocumentVersionSnapshot(
    Guid Id,
    Guid LegalDocumentId,
    string LegalDocumentName,
    int LegalDocumentGracePeriodDays,
    string VersionNumber,
    IReadOnlyDictionary<string, string> Content,
    Instant EffectiveFrom,
    bool RequiresReConsent,
    Instant CreatedAt,
    string? ChangesSummary);
