using Humans.Base.Interfaces;
using NodaTime;
using Humans.Consent.Domain;

namespace Humans.Consent.Services;

internal interface IAdminLegalDocumentService : IApplicationService
{
    Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default);

    Task<AdminLegalDocumentEditDetail?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    GitHubFolderPathNormalizationResult NormalizeGitHubFolderPath(string? input);

    /// <summary>
    /// Runs the initial sync when a GitHub folder path was supplied. Returns null when the team doesn't exist.
    /// </summary>
    Task<AdminLegalDocumentCreateResult?> CreateLegalDocumentWithInitialSyncAsync(
        AdminLegalDocumentUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<LegalDocument?> ArchiveLegalDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<string?> SyncLegalDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<VersionSummaryUpdateResult> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}

internal sealed record AdminLegalDocumentEditDetail(
    Guid Id,
    string Name,
    Guid TeamId,
    bool IsRequired,
    bool IsActive,
    int GracePeriodDays,
    string? GitHubFolderPath,
    Instant LastSyncedAt,
    string? CurrentVersion,
    IReadOnlyList<AdminLegalDocumentVersionDetail> Versions);

internal sealed record AdminLegalDocumentVersionDetail(
    Guid Id,
    string VersionNumber,
    string CommitSha,
    Instant EffectiveFrom,
    Instant CreatedAt,
    string? ChangesSummary,
    bool RequiresReConsent,
    int LanguageCount,
    IReadOnlyList<string> Languages);

internal sealed record AdminLegalDocumentCreateResult(
    LegalDocument Document,
    AdminLegalDocumentInitialSyncStatus InitialSyncStatus,
    string? SyncMessage = null,
    string? SyncError = null);

internal enum AdminLegalDocumentInitialSyncStatus
{
    NoGitHubFolderPath,
    AlreadyCurrent,
    Synced,
    Failed
}

internal enum VersionSummaryUpdateResult
{
    NotFound,
    Updated,
    TooLong
}
