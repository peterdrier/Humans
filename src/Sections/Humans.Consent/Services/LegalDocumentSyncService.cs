using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Configuration;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Users.Contracts;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Resources;

namespace Humans.Consent.Services;

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
    private readonly GitHubSettings _githubSettings = githubSettings.Value;

    public Task<string?> SyncLegalDocumentAsync(
        Guid documentId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        SyncDocumentAsync(documentId, actorUserId, cancellationToken);

    public async Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default, ICollection<Exception>? failures = null)
    {
        logger.LogInformation("Starting sync of all legal documents");

        var updatedDocuments = new List<LegalDocument>();
        var notificationBatchId = Guid.NewGuid();
        var activeDocuments = await repository.GetActiveDocumentsAsync(cancellationToken);

        foreach (var document in activeDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await SyncSingleDocumentAsync(document, actorUserId: null, cancellationToken, notificationBatchId);
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
            var syncMessage = await SyncDocumentAsync(document.Id, actorUserId, cancellationToken);
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

    private static readonly ResourceManager NoticeResources = new(typeof(ConsentResource));

    internal async Task<string?> SyncDocumentAsync(
        Guid documentId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var document = await repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
        {
            logger.LogWarning("Document {DocumentId} not found", documentId);
            return null;
        }

        return await SyncSingleDocumentAsync(document, actorUserId, cancellationToken);
    }

    internal async Task<string?> SyncSingleDocumentAsync(
        LegalDocument document,
        Guid? actorUserId,
        CancellationToken cancellationToken,
        Guid? notificationBatchId = null)
    {
        if (string.IsNullOrEmpty(document.GitHubFolderPath))
        {
            throw new InvalidOperationException(
                $"Document '{document.Name}' has no GitHub Folder Path configured.");
        }

        return await SyncFolderBasedDocumentAsync(document, actorUserId, cancellationToken, notificationBatchId);
    }

    private async Task<string?> SyncFolderBasedDocumentAsync(
        LegalDocument document,
        Guid? actorUserId,
        CancellationToken cancellationToken, Guid? notificationBatchId)
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

        if (string.IsNullOrWhiteSpace(canonicalResult.Content))
        {
            logger.LogWarning("Canonical Spanish content is blank at {Path} for document {DocumentId}",
                canonicalPath, document.Id);
            throw new InvalidOperationException($"No canonical Spanish content found at '{canonicalPath}'.");
        }

        var commitSha = canonicalResult.Sha;

        var now = clock.GetCurrentInstant();

        if (string.Equals(document.CurrentCommitSha, commitSha, StringComparison.Ordinal))
        {
            logger.LogDebug("Document {Name} is up to date (SHA: {Sha})", document.Name, commitSha);
            bool? touched = null;
            try
            {
                touched = await repository.TouchLastSyncedAtAsync(document.Id, now, cancellationToken);
            }
            finally
            {
                if (touched != false)
                    cache.InvalidateAll();
            }
            return null;
        }

        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["es"] = canonicalResult.Content
        };
        foreach (var (lang, path) in languageFiles)
        {
            if (string.Equals(lang, "es", StringComparison.Ordinal))
                continue;

            var file = await gitHub.GetFileContentAsync(path, cancellationToken);
            if (file is null)
            {
                logger.LogDebug("Language file not found at {Path}", path);
                continue;
            }
            content[lang] = file.Content;
        }

        var commitMessage = await gitHub.GetCommitMessageAsync(canonicalResult.CommitSha, cancellationToken);

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
            PendingReConsentBatchId = document.IsRequired ? notificationBatchId : null,
            CreatedAt = now,
            ChangesSummary = commitMessage ?? (isNew ? "Initial version" : "Updated from GitHub")
        };

        bool? persisted = null;
        try
        {
            persisted = await repository.AddVersionAsync(
                document.Id, newVersion, commitSha, now, cancellationToken);
        }
        finally
        {
            if (persisted != false)
                cache.InvalidateAll();
        }

        if (persisted == false)
        {
            // The row vanished between read and write: no fan-out.
            logger.LogWarning(
                "Skipped version add for document {Name} ({DocumentId}): document row not found during persist.",
                document.Name, document.Id);
            return null;
        }

        document.Versions.Add(newVersion);
        document.CurrentCommitSha = commitSha;
        document.LastSyncedAt = now;

        var languages = string.Join(", ", content.Keys.Order(StringComparer.Ordinal));
        var description = $"Published '{document.Name}' {versionNumber}: commit {commitSha}, " +
            $"requires re-consent {newVersion.RequiresReConsent}, languages {languages}.";
        if (actorUserId is { } actor)
            await audit.LogAsync(ConsentAuditActions.LegalDocumentVersionPublished, "LegalDocument", document.Id,
                description, actor, relatedEntityId: newVersion.Id, relatedEntityType: "DocumentVersion");
        else
            await audit.LogAsync(ConsentAuditActions.LegalDocumentVersionPublished, "LegalDocument", document.Id,
                description, nameof(LegalDocumentSyncService),
                relatedEntityId: newVersion.Id, relatedEntityType: "DocumentVersion");

        logger.LogInformation(
            "Synced document {Name} version {Version} (SHA: {Sha}, languages: {Languages})",
            document.Name, versionNumber, commitSha, languages);

        // Publication committed; finish its notices before observing shutdown at the next document.
        if (isNew && document.IsRequired)
        {
            await TryFanoutAsync(
                document,
                ConsentNotificationSources.LegalDocumentPublished,
                "Consent_Notification_Published",
                "Consent_Notification_PublishedBody",
                CancellationToken.None);
        }

        if (newVersion.RequiresReConsent && document.IsRequired)
        {
            await TryFanoutAsync(
                document,
                ConsentNotificationSources.ReConsentRequired,
                "Consent_Notification_ReConsentRequired",
                "Consent_Notification_ReConsentRequiredBody",
                CancellationToken.None);
        }

        return $"Synced {versionNumber} with {content.Count} language(s): {languages}";
    }

    private async Task TryFanoutAsync(
        LegalDocument document,
        NotificationSourceDefinition source,
        string titleKey,
        string bodyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var team = await teamService.GetTeamAsync(document.TeamId, cancellationToken).ConfigureAwait(false);
            if (team is null)
                return;
            var memberIds = team.Members.Select(m => m.UserId).Distinct().ToList();
            if (memberIds.Count == 0)
                return;
            var recipientsByLanguage = (await userService.GetUserInfosAsync(memberIds, cancellationToken).ConfigureAwait(false))
                .Values
                .DistinctBy(u => u.Id)
                .Where(u => u.IsActive)
                .GroupBy(u => u.PreferredLanguage.IsSupportedCultureCode() ? u.PreferredLanguage : "en", StringComparer.Ordinal);
            foreach (var group in recipientsByLanguage)
            {
                try
                {
                    var culture = CultureInfo.GetCultureInfo(group.Key);
                    var title = string.Format(culture, NoticeResources.GetString(titleKey, culture)!, document.Name);
                    var body = NoticeResources.GetString(bodyKey, culture);
                    await notificationService.SendAsync(
                        source,
                        NotificationClass.Actionable,
                        NotificationPriority.High,
                        title,
                        group.Select(u => u.Id).ToList(),
                        body: body,
                        actionUrl: "/Consent",
                        actionLabel: NoticeResources.GetString("Consent_ReviewAndConsent", culture),
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to dispatch {Source} notifications for document {DocumentId} in {Culture}",
                        source, document.Id, group.Key);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch {Source} notifications for document {DocumentId}",
                source, document.Id);
        }
    }

    public async Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetDocumentsAsync(teamId, cancellationToken);
        if (documents.Count == 0)
            return [];

        var now = clock.GetCurrentInstant();

        var teamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await teamService.GetTeamsWithParentsAsync(teamIds, cancellationToken);

        return documents
            .Select(d =>
            {
                var teamName = teams.TryGetValue(d.TeamId, out var team) ? team.Name : string.Empty;
                var currentVersion = CurrentVersionAt(d, now);

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
            .ToList();
    }

    /// <summary>The version in force at <paramref name="now"/>: latest already-effective one.</summary>
    private static DocumentVersion? CurrentVersionAt(LegalDocument document, Instant now) =>
        document.Versions
            .Where(v => v.EffectiveFrom <= now)
            .MaxBy(v => v.EffectiveFrom);

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
            CurrentVersionAt(document, clock.GetCurrentInstant())?.VersionNumber,
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

    public async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredDocuments = await repository.GetActiveRequiredDocumentsAsync(cancellationToken);

        return requiredDocuments
            .Select(d => ToLatestRequiredVersionSnapshot(d, now))
            .OfType<RequiredDocumentVersionSnapshot>()
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
            .Select(d => ToLatestRequiredVersionSnapshot(d, now))
            .OfType<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    private static RequiredDocumentVersionSnapshot? ToLatestRequiredVersionSnapshot(LegalDocument document, Instant now)
    {
        var version = CurrentVersionAt(document, now);
        return version is null ? null : new(
            version.Id,
            version.LegalDocumentId,
            document.Name,
            document.GracePeriodDays,
            version.VersionNumber,
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.ChangesSummary);
    }

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
        IReadOnlyCollection<Guid>? teamIds, CancellationToken cancellationToken = default)
    {
        var documents = teamIds is null
            ? await repository.GetActiveRequiredDocumentsAsync(cancellationToken)
            : await repository.GetActiveRequiredDocumentsForTeamsAsync(teamIds, cancellationToken);
        if (documents.Count == 0) return [];

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
}
