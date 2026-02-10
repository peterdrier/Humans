using Humans.Domain.Entities;
using Humans.Domain.Enums;

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
    /// <returns>True if the document was updated.</returns>
    Task<bool> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

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
}
