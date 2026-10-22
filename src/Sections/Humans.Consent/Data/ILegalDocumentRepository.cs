using Humans.Base.Interfaces.Repositories;
using Humans.Teams.Contracts;
using NodaTime;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

internal interface ILegalDocumentRepository : IRepository
{
    /// <summary>With <c>Versions</c>. Null if it doesn't exist.</summary>
    Task<LegalDocument?> GetByIdAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>With <c>Versions</c>.</summary>
    Task<IReadOnlyList<LegalDocument>> GetDocumentsAsync(
        Guid? teamId, CancellationToken ct = default);

    /// <summary>With <c>Versions</c>.</summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(CancellationToken ct = default);

    /// <summary>With <c>Versions</c>.</summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsAsync(CancellationToken ct = default);

    /// <summary>With <c>Versions</c>.</summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>With <c>Versions</c>.</summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>With its <see cref="LegalDocument"/>.</summary>
    Task<DocumentVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct = default);

    /// <summary>Returns the saved entity, detached.</summary>
    Task<LegalDocument> AddAsync(LegalDocument document, CancellationToken ct = default);

    /// <summary>False if the document doesn't exist.</summary>
    Task<bool> UpdateAsync(
        Guid documentId,
        string name,
        Guid teamId,
        bool isRequired,
        bool isActive,
        int gracePeriodDays,
        string? gitHubFolderPath,
        CancellationToken ct = default);

    /// <summary>Sets <c>IsActive</c> false. Returns the entity detached, or null if it doesn't exist.</summary>
    Task<LegalDocument?> ArchiveAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>A sync with no content change: only <c>LastSyncedAt</c> moves. False if the document doesn't exist.</summary>
    Task<bool> TouchLastSyncedAtAsync(
        Guid documentId, Instant lastSyncedAt, CancellationToken ct = default);

    /// <summary>
    /// Also updates the document's <c>CurrentCommitSha</c> and <c>LastSyncedAt</c>, in one transaction. False if the
    /// document doesn't exist.
    /// </summary>
    Task<bool> AddVersionAsync(
        Guid documentId,
        DocumentVersion newVersion,
        string commitSha,
        Instant lastSyncedAt,
        CancellationToken ct = default);

    /// <summary>False if the version doesn't exist or belongs to another document.</summary>
    Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken ct = default);
}
