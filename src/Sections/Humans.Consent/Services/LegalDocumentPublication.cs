using System.Globalization;
using System.Resources;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

internal sealed class LegalDocumentPublication(
    ILegalDocumentRepository repository,
    IGitHubLegalDocumentConnector gitHub,
    INotificationEmitter notificationService,
    ITeamService teamService,
    IUserService userService,
    CachingLegalDocumentSyncService cache,
    IAuditLogService audit,
    IClock clock,
    ILogger logger) : IApplicationService
{
    private static readonly ResourceManager NoticeResources = new(typeof(ConsentResource));

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

        if (isNew && document.IsRequired)
        {
            await TryFanoutAsync(
                document,
                ConsentNotificationSources.LegalDocumentPublished,
                "Consent_Notification_Published",
                "Consent_Notification_PublishedBody",
                cancellationToken);
        }

        if (newVersion.RequiresReConsent && document.IsRequired)
        {
            await TryFanoutAsync(
                document,
                ConsentNotificationSources.ReConsentRequired,
                "Consent_Notification_ReConsentRequired",
                "Consent_Notification_ReConsentRequiredBody",
                cancellationToken);
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
}
