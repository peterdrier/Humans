using Humans.Application.DTOs;
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Legal;

/// <summary>
/// Application service for admin legal-document management flows.
/// </summary>
public interface IAdminLegalDocumentService
{
    /// <summary>
    /// Gets legal documents for admin listing, optionally filtered by team.
    /// Team names are stitched in memory from <c>ITeamService</c> rather
    /// than via a cross-domain <c>.Include(d => d.Team)</c>.
    /// </summary>
    Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single legal document with versions for edit/detail screens.
    /// </summary>
    Task<LegalDocument?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalizes and validates GitHub folder path input.
    /// </summary>
    GitHubFolderPathNormalizationResult NormalizeGitHubFolderPath(string? input);

    /// <summary>
    /// Creates a legal document.
    /// </summary>
    Task<LegalDocument> CreateLegalDocumentAsync(
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a legal document.
    /// </summary>
    Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives (deactivates) a legal document.
    /// </summary>
    Task<LegalDocument?> ArchiveLegalDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers synchronization for a legal document.
    /// </summary>
    Task<string?> SyncLegalDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates change summary text for a specific document version.
    /// </summary>
    Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts active, required legal documents. Used by
    /// <c>LegalMetricsRegistrar</c> to publish
    /// <c>humans.legal_documents_active</c>.
    /// </summary>
    Task<int> GetActiveRequiredDocumentCountAsync(CancellationToken cancellationToken = default);
}
