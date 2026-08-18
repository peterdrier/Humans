using Humans.Consent.Contracts;
using Humans.Consent.Domain;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// The document side of the section: GitHub sync plus every read the section's own
/// consent path needs. Sole writer for <c>legal_documents</c> and <c>document_versions</c>
/// (nobodies-collective/Humans#751); the admin write surface is
/// <see cref="IAdminLegalDocumentService"/> on the same instance.
/// </summary>
/// <remarks>
/// Internal: two members return <see cref="LegalDocument"/> itself, and the rest serve the
/// consent dashboard and review pages. The three reads that cross the boundary are on
/// <see cref="ILegalDocumentSyncServiceRead"/>, which this inherits so nothing in the
/// section had to change.
/// </remarks>
internal interface ILegalDocumentSyncService : ILegalDocumentSyncServiceRead
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
    Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a document version by ID.
    /// </summary>
    /// <param name="versionId">The version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document version if found.</returns>
    Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every active+required legal document whose <c>TeamId</c> is in
    /// <paramref name="teamIds"/>, with team names stitched by TeamId and
    /// <see cref="LegalDocument.Versions"/> populated. Used by the consent
    /// dashboard to build the "documents per team" grouping without crossing
    /// the section boundary into <c>DbContext.LegalDocuments</c> from the
    /// Application layer.
    /// </summary>
    Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default);
}

internal sealed record ActiveRequiredLegalDocumentSnapshot(
    Guid Id,
    string Name,
    Guid TeamId,
    string TeamName,
    Instant LastSyncedAt,
    IReadOnlyList<LegalDocumentVersionSnapshot> Versions);

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
