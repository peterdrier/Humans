using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Teams.Contracts;
using NodaTime;
using System.Text.RegularExpressions;

namespace Humans.Consent.Services;

/// <summary>
/// Evicts the section's cache itself after each write rather than in the decorator's pass-through: the eviction
/// must land before the version-publish fan-out and before the next document of a full sync.
/// </summary>
internal sealed partial class LegalDocumentAdministration(
    ILegalDocumentRepository repository,
    ITeamService teamService,
    CachingLegalDocumentSyncService cache,
    IAuditLogService audit,
    IClock clock,
    ILogger<LegalDocumentSyncService> logger,
    GitHubSettings githubSettings,
    LegalDocumentPublication publication) : IApplicationService
{
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

        if (!string.Equals(owner, githubSettings.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repo, githubSettings.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubFolderPathNormalizationResult(
                false,
                null,
                $"URL points to {owner}/{repo}, but the configured repository is {githubSettings.Owner}/{githubSettings.Repository}.");
        }

        if (!string.Equals(branch, githubSettings.Branch, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubFolderPathNormalizationResult(
                false,
                null,
                $"URL points to branch '{branch}', but the configured branch is '{githubSettings.Branch}'.");
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
            var syncMessage = await publication.SyncDocumentAsync(document.Id, actorUserId, cancellationToken);
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
}
