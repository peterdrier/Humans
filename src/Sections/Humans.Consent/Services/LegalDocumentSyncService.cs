using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Humans.Consent.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

/// <summary>
/// Sole writer for the Legal-document aggregate (<c>legal_documents</c>,
/// <c>document_versions</c>). Owns both the admin write surface
/// (<see cref="IAdminLegalDocumentService"/> — create/update/archive/version-summary
/// edits from <c>/Legal/Admin/Documents</c>) and the GitHub-sync write surface
/// (<see cref="ILegalDocumentSyncService"/> — version add, sync touch). Being the
/// single writer lets it call <see cref="ILegalDocumentCacheInvalidator.InvalidateAll"/>
/// directly after each successful repository write instead of relying on a
/// cross-cutting SaveChanges interceptor (nobodies-collective/Humans#751).
/// </summary>
internal sealed partial class LegalDocumentSyncService(
    ILegalDocumentRepository repository,
    IGitHubLegalDocumentConnector gitHub,
    INotificationEmitter notificationService,
    ITeamService teamService,
    IUserServiceRead userService,
    ILegalDocumentCacheInvalidator invalidator,
    IOptions<GitHubSettings> githubSettings,
    IClock clock,
    ILogger<LegalDocumentSyncService> logger) : ILegalDocumentSyncService, IAdminLegalDocumentService
{
    private readonly GitHubSettings _githubSettings = githubSettings.Value;

    // ==========================================================================
    // IAdminLegalDocumentService — admin write/read surface
    // ==========================================================================

    public async Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetDocumentsAsync(teamId, cancellationToken);
        if (documents.Count == 0)
            return [];

        var now = clock.GetCurrentInstant();

        // Stitch team names in memory (no .Include).
        var teamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await teamService.GetTeamsWithParentsAsync(teamIds, cancellationToken);

        return documents
            .Select(d =>
            {
                var teamName = teams.TryGetValue(d.TeamId, out var team) ? team.Name : string.Empty;
                var currentVersion = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);

                Instant? lastSyncedAt = d.LastSyncedAt == default ? null : d.LastSyncedAt;

                return new AdminLegalDocumentListItem(
                    d.Id,
                    d.Name,
                    d.TeamId,
                    teamName,
                    d.IsRequired,
                    d.IsActive,
                    d.GracePeriodDays,
                    d.GitHubFolderPath,
                    currentVersion?.VersionNumber,
                    lastSyncedAt,
                    d.Versions.Count);
            })
            .OrderBy(item => item.TeamName, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<AdminLegalDocumentEditDetail?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var document = await repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
            return null;

        return new AdminLegalDocumentEditDetail(
            document.Id,
            document.Name,
            document.TeamId,
            document.IsRequired,
            document.IsActive,
            document.GracePeriodDays,
            document.GitHubFolderPath,
            document.LastSyncedAt,
            document.Versions
                .Select(v => new AdminLegalDocumentVersionDetail(
                    v.Id,
                    v.VersionNumber,
                    v.CommitSha,
                    v.EffectiveFrom,
                    v.CreatedAt,
                    v.ChangesSummary,
                    v.RequiresReConsent,
                    v.Content.Count,
                    v.Content.Keys.Order(StringComparer.Ordinal).ToList()))
                .ToList());
    }

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

    public async Task<LegalDocument> CreateLegalDocumentAsync(
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

        var created = await repository.AddAsync(document, cancellationToken);
        invalidator.InvalidateAll();
        return created;
    }

    public async Task<AdminLegalDocumentCreateResult> CreateLegalDocumentWithInitialSyncAsync(
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = await CreateLegalDocumentAsync(request, cancellationToken);

        if (string.IsNullOrWhiteSpace(document.GitHubFolderPath))
        {
            return new AdminLegalDocumentCreateResult(
                document,
                AdminLegalDocumentInitialSyncStatus.NoGitHubFolderPath);
        }

        try
        {
            var syncMessage = await SyncDocumentAsync(document.Id, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var updated = await repository.UpdateAsync(
            documentId,
            request.Name,
            request.TeamId,
            request.IsRequired,
            request.IsActive,
            request.GracePeriodDays,
            request.GitHubFolderPath,
            cancellationToken);

        if (!updated)
            return null;

        invalidator.InvalidateAll();
        return await repository.GetByIdAsync(documentId, cancellationToken);
    }

    public async Task<LegalDocument?> ArchiveLegalDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var archived = await repository.ArchiveAsync(documentId, cancellationToken);
        if (archived is not null)
            invalidator.InvalidateAll();
        return archived;
    }

    public Task<string?> SyncLegalDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        SyncDocumentAsync(documentId, cancellationToken);

    public async Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken cancellationToken = default)
    {
        var updated = await repository.UpdateVersionSummaryAsync(
            documentId, versionId, changesSummary, cancellationToken);

        if (updated)
            invalidator.InvalidateAll();

        return updated;
    }

    [GeneratedRegex(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/tree/(?<branch>[^/]+)/(?<path>[^\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex GitHubUrlPattern();

    // ==========================================================================
    // ILegalDocumentSyncService — GitHub-sync write/read surface
    // ==========================================================================

    public async Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting sync of all legal documents");

        var updatedDocuments = new List<LegalDocument>();
        var activeDocuments = await repository.GetActiveDocumentsAsync(cancellationToken);

        foreach (var document in activeDocuments)
        {
            try
            {
                var result = await SyncSingleDocumentAsync(document, cancellationToken);
                if (result is not null)
                {
                    updatedDocuments.Add(document);
                }
            }
            catch (GitHubApiException ex) when (ex.IsTransient)
            {
                // Transient GitHub outage — the next scheduled run self-heals. Log without
                // the exception object so GitHub's HTML error page stays out of the logs
                // (nobodies-collective/Humans#927).
                logger.LogWarning(
                    "Transient GitHub error syncing document {DocumentName} ({DocumentId}): {StatusCode}, will retry next run",
                    document.Name, document.Id, ex.StatusCode);
            }
            catch (GitHubApiException ex)
            {
                // Permanent (auth/scope/config) — repeats every run until a human acts, so
                // escalate to Error; still status-code only to keep the HTML body out.
                logger.LogError(
                    "GitHub API error syncing document {DocumentName} ({DocumentId}): {StatusCode} — not transient, check GitHub token/config",
                    document.Name, document.Id, ex.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error syncing document {DocumentName} ({DocumentId})",
                    document.Name, document.Id);
            }
        }

        return updatedDocuments;
    }

    public async Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
        {
            logger.LogWarning("Document {DocumentId} not found", documentId);
            return null;
        }

        return await SyncSingleDocumentAsync(document, cancellationToken);
    }

    public async Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var documentsWithUpdates = new List<LegalDocument>();
        var documents = await repository.GetActiveDocumentsAsync(cancellationToken);

        foreach (var document in documents)
        {
            try
            {
                var checkPath = !string.IsNullOrEmpty(document.GitHubFolderPath)
                    ? await GetCanonicalFilePathAsync(document.GitHubFolderPath, cancellationToken)
                    : null;

                if (string.IsNullOrEmpty(checkPath)) continue;

                var latestSha = await gitHub.GetLatestCommitShaAsync(checkPath, cancellationToken);
                if (latestSha is not null && !string.Equals(latestSha, document.CurrentCommitSha, StringComparison.Ordinal))
                {
                    documentsWithUpdates.Add(document);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error checking for updates to {DocumentName}", document.Name);
            }
        }

        return documentsWithUpdates;
    }

    public async Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetActiveDocumentsAsync(cancellationToken);
        return documents.Select(ToDocumentSnapshot).ToList();
    }

    public async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredDocuments = await repository.GetActiveRequiredDocumentsAsync(cancellationToken);

        return requiredDocuments
            .Select(d =>
            {
                var latest = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);
                if (latest is null) return null;

                return ToRequiredVersionSnapshot(latest, d);
            })
            .Where(v => v is not null)
            .Cast<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    public async Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(
        Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await repository.GetVersionByIdAsync(versionId, cancellationToken);
        return version is null ? null : ToVersionSnapshot(version);
    }

    public async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredDocuments = await repository.GetActiveRequiredDocumentsForTeamAsync(teamId, cancellationToken);

        return requiredDocuments
            .Select(d =>
            {
                var latest = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);
                if (latest is null) return null;
                return ToRequiredVersionSnapshot(latest, d);
            })
            .Where(v => v is not null)
            .Cast<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    private static RequiredDocumentVersionSnapshot ToRequiredVersionSnapshot(DocumentVersion version, LegalDocument document) =>
        new(
            version.Id,
            version.LegalDocumentId,
            document.Name,
            document.GracePeriodDays,
            version.VersionNumber,
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.ChangesSummary);

    private static LegalDocumentVersionSnapshot ToVersionSnapshot(DocumentVersion version) =>
        new(
            version.Id,
            version.LegalDocumentId,
            version.LegalDocument.Name,
            version.LegalDocument.GracePeriodDays,
            version.VersionNumber,
            new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.CreatedAt,
            version.ChangesSummary);

    public async Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetActiveRequiredDocumentsAsync(cancellationToken);
        return documents.Count;
    }

    public async Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetActiveRequiredDocumentsForTeamsAsync(teamIds, cancellationToken);
        if (documents.Count == 0) return [];

        // Team names via ITeamService — no cross-section EF join (memory/architecture/no-cross-section-ef-joins.md).
        var distinctTeamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await teamService.GetTeamsWithParentsAsync(distinctTeamIds, cancellationToken);

        return documents.Select(d =>
        {
            var teamName = teams.TryGetValue(d.TeamId, out var team) ? team.Name : string.Empty;
            return new ActiveRequiredLegalDocumentSnapshot(
                d.Id,
                d.Name,
                d.TeamId,
                teamName,
                d.LastSyncedAt,
                d.Versions.Select(ToVersionSnapshot).ToList());
        }).ToList();
    }

    private static LegalDocumentSnapshot ToDocumentSnapshot(LegalDocument document) =>
        new(
            document.Id,
            document.Name,
            document.TeamId,
            document.GracePeriodDays,
            document.GitHubFolderPath,
            document.CurrentCommitSha,
            document.IsRequired,
            document.IsActive,
            document.CreatedAt,
            document.LastSyncedAt,
            document.Versions.Select(ToVersionSnapshot).ToList());

    private async Task<string?> SyncSingleDocumentAsync(
        LegalDocument document,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(document.GitHubFolderPath))
        {
            throw new InvalidOperationException(
                $"Document '{document.Name}' has no GitHub Folder Path configured.");
        }

        return await SyncFolderBasedDocumentAsync(document, cancellationToken);
    }

    private async Task<string?> SyncFolderBasedDocumentAsync(
        LegalDocument document,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Syncing document {Name} from folder {Folder}",
            document.Name, document.GitHubFolderPath);

        var languageFiles = await gitHub.DiscoverLanguageFilesAsync(
            document.GitHubFolderPath!, cancellationToken);

        if (languageFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"No markdown files found in folder '{document.GitHubFolderPath}'. " +
                "Expected files like 'name.md' (Spanish) or 'name-en.md' (English).");
        }

        if (!languageFiles.TryGetValue("es", out var canonicalPath))
        {
            var found = string.Join(", ", languageFiles.Keys);
            throw new InvalidOperationException(
                $"No canonical Spanish file found in folder '{document.GitHubFolderPath}'. " +
                $"Need a file without language suffix (e.g. 'name.md'). Found languages: {found}");
        }

        var canonicalResult = await gitHub.GetFileContentAsync(canonicalPath, cancellationToken);
        if (canonicalResult is null)
        {
            logger.LogWarning("Canonical file not found at {Path}", canonicalPath);
            return null;
        }

        var commitSha = canonicalResult.Sha;

        var now = clock.GetCurrentInstant();

        if (string.Equals(document.CurrentCommitSha, commitSha, StringComparison.Ordinal))
        {
            logger.LogDebug("Document {Name} is up to date (SHA: {Sha})", document.Name, commitSha);
            var touched = await repository.TouchLastSyncedAtAsync(document.Id, now, cancellationToken);
            if (touched)
                invalidator.InvalidateAll();
            return null;
        }

        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lang, path) in languageFiles)
        {
            var file = await gitHub.GetFileContentAsync(path, cancellationToken);
            if (file is null)
            {
                logger.LogDebug("Language file not found at {Path}", path);
                continue;
            }
            content[lang] = file.Content;
        }

        var commitMessage = await gitHub.GetCommitMessageAsync(commitSha, cancellationToken);

        var isNew = document.Versions.Count == 0;
        var versionNumber = $"v{document.Versions.Count + 1}.0";
        var newVersion = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = document.Id,
            VersionNumber = versionNumber,
            CommitSha = commitSha,
            Content = content,
            EffectiveFrom = now,
            RequiresReConsent = !isNew,
            CreatedAt = now,
            ChangesSummary = commitMessage ?? (isNew ? "Initial version" : "Updated from GitHub")
        };

        var persisted = await repository.AddVersionAsync(
            document.Id, newVersion, commitSha, now, cancellationToken);

        if (!persisted)
        {
            // Document row vanished between read and write — bail without log/fanout.
            logger.LogWarning(
                "Skipped version add for document {Name} ({DocumentId}): document row not found during persist.",
                document.Name, document.Id);
            return null;
        }

        invalidator.InvalidateAll();

        // Mirror write onto in-memory aggregate so callers see the same shape as the old tracked-entity path.
        document.Versions.Add(newVersion);
        document.CurrentCommitSha = commitSha;
        document.LastSyncedAt = now;

        var languages = string.Join(", ", content.Keys.Order(StringComparer.Ordinal));

        logger.LogInformation(
            "Synced document {Name} version {Version} (SHA: {Sha}, languages: {Languages})",
            document.Name, versionNumber, commitSha, languages);

        if (isNew && document.IsRequired)
        {
            await TryFanoutAsync(
                document,
                NotificationSource.LegalDocumentPublished,
                $"New legal document published: {document.Name}",
                "A new required legal document has been published. Please review and sign it.",
                cancellationToken);
        }

        if (newVersion.RequiresReConsent && document.IsRequired)
        {
            await TryFanoutAsync(
                document,
                NotificationSource.ReConsentRequired,
                $"{document.Name} has been updated — re-consent required",
                "A required legal document has been updated. Please review and sign the new version.",
                cancellationToken);
        }

        return $"Synced {versionNumber} with {content.Count} language(s): {languages}";
    }

    private async Task TryFanoutAsync(
        LegalDocument document,
        NotificationSource source,
        string title,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            var approvedUserIds = (await userService.GetAllUserInfosAsync(cancellationToken).ConfigureAwait(false))
                .Where(u => u.IsActive)
                .Select(u => u.Id)
                .ToList();
            if (approvedUserIds.Count > 0)
            {
                await notificationService.SendAsync(
                    source,
                    NotificationClass.Actionable,
                    NotificationPriority.High,
                    title,
                    approvedUserIds,
                    body: body,
                    actionUrl: "/Consent",
                    actionLabel: "Review document",
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch {Source} notifications for document {DocumentId}",
                source, document.Id);
        }
    }

    private async Task<string?> GetCanonicalFilePathAsync(
        string folderPath, CancellationToken cancellationToken)
    {
        var files = await gitHub.DiscoverLanguageFilesAsync(folderPath, cancellationToken);
        return files.GetValueOrDefault("es");
    }
}
