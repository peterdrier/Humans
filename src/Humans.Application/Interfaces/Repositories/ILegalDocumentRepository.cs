using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Legal Documents aggregate (<c>legal_documents</c>,
/// <c>document_versions</c>). The only non-test file that may touch those
/// DbSets after the §15 Legal document migration lands.
/// </summary>
/// <remarks>
/// Shared between <c>AdminLegalDocumentService</c> and
/// <c>LegalDocumentSyncService</c> — both live in
/// <c>Humans.Application.Services.Legal</c>. Read methods use
/// <c>AsNoTracking</c>; writes create and dispose short-lived contexts via
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>
/// so the repository can be registered Singleton alongside
/// <see cref="IProfileRepository"/>, <see cref="IUserRepository"/>, etc.
///
/// <para>
/// <see cref="Humans.Domain.Entities.LegalDocument.Team"/> is a cross-domain
/// nav that this section's invariants document schedules for strip. This
/// repository never <c>.Include</c>s it; callers that need team data call
/// <see cref="Humans.Application.Interfaces.ITeamService"/> and stitch by
/// <see cref="Humans.Domain.Entities.LegalDocument.TeamId"/>.
/// </para>
/// </remarks>
public interface ILegalDocumentRepository
{
    // ==========================================================================
    // Reads — LegalDocument
    // ==========================================================================

    /// <summary>
    /// Loads a single legal document with aggregate-local <c>Versions</c>
    /// included. Read-only (AsNoTracking). Returns null if the document does
    /// not exist.
    /// </summary>
    Task<LegalDocument?> GetByIdAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Loads legal documents, optionally filtered by team id. Includes
    /// aggregate-local <c>Versions</c>. Read-only (AsNoTracking).
    /// Ordered by document name — callers that need per-team grouping should
    /// stitch with <see cref="ITeamService"/>.
    /// </summary>
    Task<IReadOnlyList<LegalDocument>> GetDocumentsAsync(
        Guid? teamId, CancellationToken ct = default);

    /// <summary>
    /// Returns every active legal document with <c>Versions</c> included.
    /// Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every active, required legal document with <c>Versions</c>
    /// included. Read-only (AsNoTracking). Used by
    /// <see cref="Humans.Application.Interfaces.ILegalDocumentSyncService.GetRequiredVersionsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns active, required legal documents for a team with
    /// <c>Versions</c> included. Read-only (AsNoTracking). Used when
    /// computing required consents for a team membership.
    /// </summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Returns active, required legal documents for any of the given teams with
    /// <c>Team</c> and <c>Versions</c> included. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    // ==========================================================================
    // Reads — DocumentVersion
    // ==========================================================================

    /// <summary>
    /// Loads a single document version. Read-only (AsNoTracking). Includes
    /// the parent <see cref="LegalDocument"/> (aggregate-local nav) for
    /// callers that need document metadata alongside the version.
    /// </summary>
    Task<DocumentVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct = default);

    // ==========================================================================
    // Writes — LegalDocument
    // ==========================================================================

    /// <summary>
    /// Adds a new <see cref="LegalDocument"/>. Persists immediately.
    /// Returns the saved entity detached from the repository context.
    /// </summary>
    Task<LegalDocument> AddAsync(LegalDocument document, CancellationToken ct = default);

    /// <summary>
    /// Updates a legal document with the given field set. Returns true if
    /// the document was found and updated; false if it does not exist.
    /// </summary>
    Task<bool> UpdateAsync(
        Guid documentId,
        string name,
        Guid teamId,
        bool isRequired,
        bool isActive,
        int gracePeriodDays,
        string? gitHubFolderPath,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-archives a legal document (sets <c>IsActive=false</c>). Returns
    /// the updated entity detached, or null if the document does not exist.
    /// </summary>
    Task<LegalDocument?> ArchiveAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Records that a sync succeeded with no content change — updates
    /// <c>LastSyncedAt</c> only. Returns false if the document does not
    /// exist.
    /// </summary>
    Task<bool> TouchLastSyncedAtAsync(
        Guid documentId, Instant lastSyncedAt, CancellationToken ct = default);

    /// <summary>
    /// Adds a new <see cref="DocumentVersion"/> and updates the parent
    /// document's <c>CurrentCommitSha</c> and <c>LastSyncedAt</c> in a
    /// single transaction. Returns false if the document does not exist.
    /// </summary>
    Task<bool> AddVersionAsync(
        Guid documentId,
        DocumentVersion newVersion,
        string commitSha,
        Instant lastSyncedAt,
        CancellationToken ct = default);

    // ==========================================================================
    // Writes — DocumentVersion
    // ==========================================================================

    /// <summary>
    /// Updates the <c>ChangesSummary</c> of a specific version. Returns
    /// true if found and updated; false if the version does not exist or
    /// does not belong to the specified document.
    /// </summary>
    Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken ct = default);
}
