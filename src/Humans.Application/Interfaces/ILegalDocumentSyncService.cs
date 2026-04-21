using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for syncing legal documents from the GitHub repository.
/// </summary>
public interface ILegalDocumentSyncService
{
    /// <summary>
    /// Syncs all legal documents from the GitHub repository.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of documents that were updated.</returns>
    Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs a specific legal document from the GitHub repository.
    /// </summary>
    /// <param name="documentId">The document ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary message if updated, or null if already up to date.</returns>
    Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if any documents have updates available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Documents with pending updates.</returns>
    Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active legal documents.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All active legal documents.</returns>
    Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all document versions that require consent.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current versions of required documents.</returns>
    Task<IReadOnlyList<DocumentVersion>> GetRequiredVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a document version by ID.
    /// </summary>
    /// <param name="versionId">The version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document version if found.</returns>
    Task<DocumentVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current required document versions for a specific team.
    /// Returns the latest effective version per required+active document scoped to the team.
    /// Each returned DocumentVersion has its LegalDocument navigation loaded.
    /// </summary>
    Task<IReadOnlyList<DocumentVersion>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every active+required legal document whose <c>TeamId</c> is in
    /// <paramref name="teamIds"/>, with the <see cref="LegalDocument.Team"/>
    /// and <see cref="LegalDocument.Versions"/> navigation properties
    /// populated. Used by the consent dashboard to build the "documents per
    /// team" grouping without crossing the section boundary into
    /// <c>DbContext.LegalDocuments</c> from the Application layer.
    /// </summary>
    Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default);
}
