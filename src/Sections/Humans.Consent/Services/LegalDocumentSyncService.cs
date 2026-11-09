using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using System.Text.RegularExpressions;
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

/// <summary>
/// Evicts the section's cache itself after each write rather than in the decorator's pass-through: the eviction
/// must land before the version-publish fan-out and before the next document of a full sync.
/// </summary>
internal sealed partial class LegalDocumentSyncService(
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

    public Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId, CancellationToken cancellationToken = default) =>
        reader.GetLegalDocumentsAsync(teamId, cancellationToken);

    public Task<AdminLegalDocumentEditDetail?> GetLegalDocumentWithVersionsAsync(
        Guid documentId, CancellationToken cancellationToken = default) =>
        reader.GetLegalDocumentWithVersionsAsync(documentId, cancellationToken);

    public GitHubFolderPathNormalizationResult NormalizeGitHubFolderPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new GitHubFolderPathNormalizationResult(true, null, null);
        }

        input = input.Trim();
        var match = GitHubUrlPattern().Match(input);

        if (!match.Success)
        {
            return new GitHubFolderPathNormalizationResult(true, input.TrimEnd('/') + "/", null);
        }

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        var branch = match.Groups["branch"].Value;
        var path = match.Groups["path"].Value;

        if (!string.Equals(owner, _githubSettings.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repo, _githubSettings.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubFolderPathNormalizationResult(
                false,
                null,
                $"URL points to {owner}/{repo}, but the configured repository is {_githubSettings.Owner}/{_githubSettings.Repository}.");
        }

        if (!string.Equals(branch, _githubSettings.Branch, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubFolderPathNormalizationResult(
                false,
                null,
                $"URL points to branch '{branch}', but the configured branch is '{_githubSettings.Branch}'.");
        }

        return new GitHubFolderPathNormalizationResult(true, path.TrimEnd('/') + "/", null);
    }

    private async Task<LegalDocument> CreateLegalDocumentAsync(
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            TeamId = request.TeamId,
            IsRequired = request.IsRequired,
            IsActive = request.IsActive,
            GracePeriodDays = request.GracePeriodDays,
            GitHubFolderPath = request.GitHubFolderPath,
            CurrentCommitSha = string.Empty,
            CreatedAt = clock.GetCurrentInstant()
        };

        try
        {
            return await repository.AddAsync(document, cancellationToken);
        }
        finally
        {
            cache.InvalidateAll();
        }
    }

    public async Task<AdminLegalDocumentCreateResult?> CreateLegalDocumentWithInitialSyncAsync(
        AdminLegalDocumentUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (await teamService.GetTeamAsync(request.TeamId, cancellationToken) is null)
        {
            logger.LogWarning("Rejected legal-document creation for missing team {TeamId} by user {ActorUserId}",
                request.TeamId, actorUserId);
            return null;
        }

        var document = await CreateLegalDocumentAsync(request, cancellationToken);
        await audit.LogAsync(ConsentAuditActions.LegalDocumentCreated, "LegalDocument", document.Id,
            $"Created '{document.Name}': team {document.TeamId}, required {document.IsRequired}, " +
            $"active {document.IsActive}, grace period {document.GracePeriodDays} days, folder '{document.GitHubFolderPath}'.",
            actorUserId);

        if (string.IsNullOrWhiteSpace(document.GitHubFolderPath))
        {
            return new AdminLegalDocumentCreateResult(
                document,
                AdminLegalDocumentInitialSyncStatus.NoGitHubFolderPath);
        }

        try
        {
            var syncMessage = await SyncDocumentCoreAsync(document.Id, actorUserId, cancellationToken);
            return new AdminLegalDocumentCreateResult(
                document,
                syncMessage is null
                    ? AdminLegalDocumentInitialSyncStatus.AlreadyCurrent
                    : AdminLegalDocumentInitialSyncStatus.Synced,
                syncMessage);
        }
        catch (Exception ex)
        {
            return new AdminLegalDocumentCreateResult(
                document,
                AdminLegalDocumentInitialSyncStatus.Failed,
                SyncError: ex.Message);
        }
    }

    public async Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (await teamService.GetTeamAsync(request.TeamId, cancellationToken) is null)
        {
            logger.LogWarning("Rejected legal-document edit {DocumentId} for missing team {TeamId} by user {ActorUserId}",
                documentId, request.TeamId, actorUserId);
            return null;
        }

        bool? updated = null;
        try
        {
            updated = await repository.UpdateAsync(
                documentId,
                request.Name,
                request.TeamId,
                request.IsRequired,
                request.IsActive,
                request.GracePeriodDays,
                request.GitHubFolderPath,
                cancellationToken);
        }
        finally
        {
            // Unknown completion can mean the write committed before its acknowledgement failed.
            if (updated != false)
                cache.InvalidateAll();
        }

        if (updated == false)
            return null;

        await audit.LogAsync(ConsentAuditActions.LegalDocumentUpdated, "LegalDocument", documentId,
            $"Updated '{request.Name}': team {request.TeamId}, required {request.IsRequired}, " +
            $"active {request.IsActive}, grace period {request.GracePeriodDays} days, folder '{request.GitHubFolderPath}'.",
            actorUserId);
        return await repository.GetByIdAsync(documentId, cancellationToken);
    }

    public async Task<LegalDocument?> ArchiveLegalDocumentAsync(
        Guid documentId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        LegalDocument? archived;
        bool? changed = null;
        try
        {
            archived = await repository.ArchiveAsync(documentId, cancellationToken);
            changed = archived is not null;
        }
        finally
        {
            if (changed != false)
                cache.InvalidateAll();
        }
        if (archived is not null)
        {
            await audit.LogAsync(ConsentAuditActions.LegalDocumentArchived, "LegalDocument", documentId,
                $"Archived '{archived.Name}'.", actorUserId);
        }
        return archived;
    }

    public Task<string?> SyncLegalDocumentAsync(
        Guid documentId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        SyncDocumentCoreAsync(documentId, actorUserId, cancellationToken);

    public async Task<VersionSummaryUpdateResult> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var summary = string.IsNullOrWhiteSpace(changesSummary) ? null : changesSummary.Trim();
        if (summary?.Length > 2000)
        {
            logger.LogWarning("Rejected oversized summary for legal document {DocumentId}, version {VersionId}",
                documentId, versionId);
            return VersionSummaryUpdateResult.TooLong;
        }

        bool? updated = null;
        try
        {
            updated = await repository.UpdateVersionSummaryAsync(
                documentId, versionId, summary, cancellationToken);
        }
        finally
        {
            if (updated != false)
                cache.InvalidateAll();
        }

        if (updated == true)
        {
            await audit.LogAsync(ConsentAuditActions.LegalDocumentVersionSummaryUpdated, "LegalDocument", documentId,
                $"Updated summary of version {versionId} to '{summary}'.", actorUserId,
                relatedEntityId: versionId, relatedEntityType: "DocumentVersion");
        }

        return updated == true ? VersionSummaryUpdateResult.Updated : VersionSummaryUpdateResult.NotFound;
    }

    [GeneratedRegex(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/tree/(?<branch>[^/]+)/(?<path>[^\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex GitHubUrlPattern();

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
        SyncDocumentCoreAsync(documentId, actorUserId: null, cancellationToken);

    private async Task<string?> SyncDocumentCoreAsync(
        Guid documentId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var document = await repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
        {
            logger.LogWarning("Document {DocumentId} not found", documentId);
            return null;
        }

        return await publication.SyncSingleDocumentAsync(document, actorUserId, cancellationToken);
    }

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
