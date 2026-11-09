using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Configuration;
using Humans.Consent.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

internal sealed class LegalDocumentSyncService(
    ILegalDocumentRepository repository,
    IGitHubLegalDocumentConnector gitHub,
    INotificationEmitter notificationService,
    ITeamService teamService,
    IUserService userService,
    CachingLegalDocumentSyncService cache,
    IAuditLogService audit,
    IOptions<GitHubSettings> githubSettings,
    IClock clock,
    ILogger<LegalDocumentSyncService> logger) : ILegalDocumentSyncManagement, IAdminLegalDocumentService
{
    private readonly LegalDocumentReader reader = new(repository, teamService, clock);
    private readonly LegalDocumentPublication publication = new(
        repository, gitHub, notificationService, teamService, userService, cache, audit, clock, logger);

    private readonly GitHubSettings _githubSettings = githubSettings.Value;
    private LegalDocumentAdministration Administration => new(
        repository, teamService, cache, audit, clock, logger, _githubSettings, publication);

    public Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId, CancellationToken cancellationToken = default) =>
        reader.GetLegalDocumentsAsync(teamId, cancellationToken);

    public Task<AdminLegalDocumentEditDetail?> GetLegalDocumentWithVersionsAsync(
        Guid documentId, CancellationToken cancellationToken = default) =>
        reader.GetLegalDocumentWithVersionsAsync(documentId, cancellationToken);

    public GitHubFolderPathNormalizationResult NormalizeGitHubFolderPath(string? input) =>
        Administration.NormalizeGitHubFolderPath(input);


    public Task<AdminLegalDocumentCreateResult?> CreateLegalDocumentWithInitialSyncAsync(
        AdminLegalDocumentUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        Administration.CreateLegalDocumentWithInitialSyncAsync(request, actorUserId, cancellationToken);

    public Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        Administration.UpdateLegalDocumentAsync(documentId, request, actorUserId, cancellationToken);

    public Task<LegalDocument?> ArchiveLegalDocumentAsync(
        Guid documentId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        Administration.ArchiveLegalDocumentAsync(documentId, actorUserId, cancellationToken);

    public Task<string?> SyncLegalDocumentAsync(
        Guid documentId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        publication.SyncDocumentAsync(documentId, actorUserId, cancellationToken);

    public Task<VersionSummaryUpdateResult> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        Administration.UpdateVersionSummaryAsync(documentId, versionId, changesSummary, actorUserId, cancellationToken);


    public async Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default, ICollection<Exception>? failures = null)
    {
        logger.LogInformation("Starting sync of all legal documents");

        var updatedDocuments = new List<LegalDocument>();
        var notificationBatchId = Guid.NewGuid();
        var activeDocuments = await repository.GetActiveDocumentsAsync(cancellationToken);

        foreach (var document in activeDocuments)
        {
            try
            {
                var result = await publication.SyncSingleDocumentAsync(document, actorUserId: null, cancellationToken, notificationBatchId);
                if (result is not null)
                {
                    updatedDocuments.Add(document);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GitHubApiException ex) when (ex.IsTransient)
            {
                // The next run self-heals. Neither logs nor the job's aggregate may carry GitHub's HTML error page.
                failures?.Add(new InvalidOperationException(
                    $"GitHub sync failed for legal document {document.Id} (HTTP {(int)ex.StatusCode})."));
                logger.LogWarning(
                    "Transient GitHub error syncing document {DocumentName} ({DocumentId}): {StatusCode}, will retry next run",
                    document.Name, document.Id, ex.StatusCode);
            }
            catch (GitHubApiException ex)
            {
                // Repeats every run until a human acts, so Error; still status code only.
                failures?.Add(new InvalidOperationException(
                    $"GitHub sync failed for legal document {document.Id} (HTTP {(int)ex.StatusCode})."));
                logger.LogError(
                    "GitHub API error syncing document {DocumentName} ({DocumentId}): {StatusCode} — not transient, check GitHub token/config",
                    document.Name, document.Id, ex.StatusCode);
            }
            catch (Exception ex)
            {
                failures?.Add(ex);
                logger.LogError(ex, "Error syncing document {DocumentName} ({DocumentId})",
                    document.Name, document.Id);
            }
        }

        return updatedDocuments;
    }

    public Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        publication.SyncDocumentAsync(documentId, actorUserId: null, cancellationToken);


    public Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default) =>
        reader.GetActiveDocumentsAsync(cancellationToken);

    public Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default) =>
        reader.GetRequiredVersionsAsync(cancellationToken);

    public Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(
        Guid versionId, CancellationToken cancellationToken = default) =>
        reader.GetVersionByIdAsync(versionId, cancellationToken);

    public Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default) =>
        reader.GetRequiredDocumentVersionsForTeamAsync(teamId, cancellationToken);

    public Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default) =>
        reader.GetActiveRequiredCountAsync(cancellationToken);

    public Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid>? teamIds, CancellationToken cancellationToken = default) =>
        reader.GetActiveRequiredDocumentsForTeamsAsync(teamIds, cancellationToken);
}
