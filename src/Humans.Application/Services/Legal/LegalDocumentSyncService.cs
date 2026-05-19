using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Legal;

public sealed class LegalDocumentSyncService(
    ILegalDocumentRepository repository,
    IGitHubLegalDocumentConnector gitHub,
    INotificationService notificationService,
    ITeamService teamService,
    IUserService userService,
    IClock clock,
    ILogger<LegalDocumentSyncService> logger) : ILegalDocumentSyncService
{
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
        var teams = await teamService.GetByIdsWithParentsAsync(distinctTeamIds, cancellationToken);

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
            await repository.TouchLastSyncedAtAsync(document.Id, now, cancellationToken);
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
                    actionUrl: "/Legal/Consent",
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

