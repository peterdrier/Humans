using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Legal;

/// <summary>
/// Application-layer implementation of <see cref="ILegalDocumentSyncService"/>.
/// Persists every legal-document mutation through
/// <see cref="ILegalDocumentRepository"/> and delegates all GitHub I/O to
/// <see cref="IGitHubLegalDocumentConnector"/>. The section's external-API
/// side lives in <c>Humans.Infrastructure</c> as the connector
/// implementation; everything domain-shaped lives here.
/// </summary>
public sealed class LegalDocumentSyncService : ILegalDocumentSyncService
{
    private readonly ILegalDocumentRepository _repository;
    private readonly IGitHubLegalDocumentConnector _gitHub;
    private readonly INotificationService _notificationService;
    private readonly IProfileService _profileService;
    private readonly IClock _clock;
    private readonly ILogger<LegalDocumentSyncService> _logger;

    public LegalDocumentSyncService(
        ILegalDocumentRepository repository,
        IGitHubLegalDocumentConnector gitHub,
        INotificationService notificationService,
        IProfileService profileService,
        IClock clock,
        ILogger<LegalDocumentSyncService> logger)
    {
        _repository = repository;
        _gitHub = gitHub;
        _notificationService = notificationService;
        _profileService = profileService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting sync of all legal documents");

        var updatedDocuments = new List<LegalDocument>();
        var activeDocuments = await _repository.GetActiveDocumentsAsync(cancellationToken);

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
                _logger.LogError(ex, "Error syncing document {DocumentName} ({DocumentId})",
                    document.Name, document.Id);
            }
        }

        return updatedDocuments;
    }

    public async Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await _repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
        {
            _logger.LogWarning("Document {DocumentId} not found", documentId);
            return null;
        }

        return await SyncSingleDocumentAsync(document, cancellationToken);
    }

    public async Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var documentsWithUpdates = new List<LegalDocument>();
        var documents = await _repository.GetActiveDocumentsAsync(cancellationToken);

        foreach (var document in documents)
        {
            try
            {
                var checkPath = !string.IsNullOrEmpty(document.GitHubFolderPath)
                    ? await GetCanonicalFilePathAsync(document.GitHubFolderPath, cancellationToken)
                    : null;

                if (string.IsNullOrEmpty(checkPath)) continue;

                var latestSha = await _gitHub.GetLatestCommitShaAsync(checkPath, cancellationToken);
                if (latestSha is not null && !string.Equals(latestSha, document.CurrentCommitSha, StringComparison.Ordinal))
                {
                    documentsWithUpdates.Add(document);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking for updates to {DocumentName}", document.Name);
            }
        }

        return documentsWithUpdates;
    }

    public Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default) =>
        _repository.GetActiveDocumentsAsync(cancellationToken);

    public async Task<IReadOnlyList<DocumentVersion>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var requiredDocuments = await _repository.GetActiveRequiredDocumentsAsync(cancellationToken);

        return requiredDocuments
            .Select(d =>
            {
                var latest = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);
                if (latest is null) return null;

                // Populate the aggregate-local parent nav so callers (e.g.
                // SendReConsentReminderJob.ExecuteAsync) can read
                // version.LegalDocument.Name without re-querying.
                latest.LegalDocument = d;
                return latest;
            })
            .Where(v => v is not null)
            .Cast<DocumentVersion>()
            .ToList();
    }

    public Task<DocumentVersion?> GetVersionByIdAsync(
        Guid versionId, CancellationToken cancellationToken = default) =>
        _repository.GetVersionByIdAsync(versionId, cancellationToken);

    public async Task<IReadOnlyList<DocumentVersion>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var requiredDocuments = await _repository.GetActiveRequiredDocumentsForTeamAsync(teamId, cancellationToken);

        return requiredDocuments
            .Select(d =>
            {
                var latest = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);
                if (latest is null) return null;
                latest.LegalDocument = d;
                return latest;
            })
            .Where(v => v is not null)
            .Cast<DocumentVersion>()
            .ToList();
    }

    public Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default) =>
        _repository.GetActiveRequiredDocumentsForTeamsAsync(teamIds, cancellationToken);

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
        _logger.LogDebug("Syncing document {Name} from folder {Folder}",
            document.Name, document.GitHubFolderPath);

        var languageFiles = await _gitHub.DiscoverLanguageFilesAsync(
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

        // Fetch canonical file to get commit SHA
        var canonicalResult = await _gitHub.GetFileContentAsync(canonicalPath, cancellationToken);
        if (canonicalResult is null)
        {
            _logger.LogWarning("Canonical file not found at {Path}", canonicalPath);
            return null;
        }

        var commitSha = canonicalResult.Sha;

        var now = _clock.GetCurrentInstant();

        if (string.Equals(document.CurrentCommitSha, commitSha, StringComparison.Ordinal))
        {
            _logger.LogDebug("Document {Name} is up to date (SHA: {Sha})", document.Name, commitSha);
            await _repository.TouchLastSyncedAtAsync(document.Id, now, cancellationToken);
            return null;
        }

        // Fetch all language files
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lang, path) in languageFiles)
        {
            var file = await _gitHub.GetFileContentAsync(path, cancellationToken);
            if (file is null)
            {
                _logger.LogDebug("Language file not found at {Path}", path);
                continue;
            }
            content[lang] = file.Content;
        }

        var commitMessage = await _gitHub.GetCommitMessageAsync(commitSha, cancellationToken);

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

        var persisted = await _repository.AddVersionAsync(
            document.Id, newVersion, commitSha, now, cancellationToken);

        if (!persisted)
        {
            // Document row vanished between the read above and the write —
            // skip the in-memory mutation, success log, and notification
            // fan-out so we don't advertise a sync that didn't happen.
            _logger.LogWarning(
                "Skipped version add for document {Name} ({DocumentId}): document row not found during persist.",
                document.Name, document.Id);
            return null;
        }

        // Mirror the write back onto the in-memory aggregate so callers of
        // SyncAllDocumentsAsync observe the same shape they used to get when
        // the service mutated a tracked entity in place.
        document.Versions.Add(newVersion);
        document.CurrentCommitSha = commitSha;
        document.LastSyncedAt = now;

        var languages = string.Join(", ", content.Keys.Order(StringComparer.Ordinal));

        _logger.LogInformation(
            "Synced document {Name} version {Version} (SHA: {Sha}, languages: {Languages})",
            document.Name, versionNumber, commitSha, languages);

        // Fan-out notifications — route cross-section reads through IProfileService
        // rather than hitting _dbContext.Profiles directly.
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
            var approvedUserIds = await _profileService.GetActiveApprovedUserIdsAsync(cancellationToken);
            if (approvedUserIds.Count > 0)
            {
                await _notificationService.SendAsync(
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
            _logger.LogError(ex,
                "Failed to dispatch {Source} notifications for document {DocumentId}",
                source, document.Id);
        }
    }

    private async Task<string?> GetCanonicalFilePathAsync(
        string folderPath, CancellationToken cancellationToken)
    {
        var files = await _gitHub.DiscoverLanguageFilesAsync(folderPath, cancellationToken);
        return files.GetValueOrDefault("es");
    }
}
