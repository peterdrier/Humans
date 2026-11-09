using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Humans.Base.Extensions;
using Humans.Teams.Contracts;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

internal sealed class LegalDocumentSyncRunner(
    ILegalDocumentSyncManagement syncService,
    IEmailService emailService,
    ConsentEmails emailMessages,
    ITeamService teamService,
    IUserService userService,
    IConsentService consents,
    ILegalDocumentRepository documents,
    ILogger<LegalDocumentSyncRunner> logger) : ILegalDocumentSyncRunner
{
    public async Task SyncAndNotifyAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        var updatedDocs = await syncService.SyncAllDocumentsAsync(cancellationToken, failures);
        logger.LogInformation("Synced {Count} updated legal documents", updatedDocs.Count);

        // The version and its marker commit together, so a crash or a later unchanged sync cannot lose this work.
        var pending = (await documents.GetDocumentsAsync(null, cancellationToken))
            .SelectMany(document => document.Versions
                .Where(version => version.PendingReConsentBatchId is not null)
                .Select(version => (Document: document, Version: version)))
            .GroupBy(change => change.Version.PendingReConsentBatchId!.Value);
        foreach (var batch in pending)
        {
            try
            {
                var currentChanges = batch.Where(change => change.Document.IsActive && change.Document.IsRequired
                    && change.Document.CurrentVersion?.Id == change.Version.Id).ToList();
                var batchFailures = await SendReConsentNotificationsAsync(batch.Key, currentChanges, cancellationToken);
                failures.AddRange(batchFailures);
                if (batchFailures.Count == 0)
                    await documents.CompleteReConsentBatchAsync(batch.Key, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Failed to complete legal document notification batch {BatchId}", batch.Key);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Failed to complete legal document sync or notification batches.", failures);
    }

    private async Task<IReadOnlyList<Exception>> SendReConsentNotificationsAsync(
        Guid batchId, IReadOnlyList<(LegalDocument Document, DocumentVersion Version)> changes,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        var userDocuments = new Dictionary<Guid, List<(LegalDocument Document, DocumentVersion Version)>>();
        foreach (var teamDocuments in changes.GroupBy(change => change.Document.TeamId))
        {
            try
            {
                var team = await teamService.GetTeamAsync(teamDocuments.Key, cancellationToken);
                if (team is null) continue;
                foreach (var userId in team.Members.Select(member => member.UserId).Distinct())
                {
                    if (!userDocuments.TryGetValue(userId, out var required))
                        userDocuments.Add(userId, required = []);
                    required.AddRange(teamDocuments);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Failed to resolve team {TeamId} for legal document notification batch {BatchId}",
                    teamDocuments.Key, batchId);
            }
        }
        if (userDocuments.Count == 0)
        {
            logger.LogInformation("No team members to notify for legal document batch {BatchId}", batchId);
            return failures;
        }

        var users = await userService.GetUserInfosAsync(userDocuments.Keys.ToList(), cancellationToken);
        var recipients = userDocuments.Keys
            .Where(id => users.TryGetValue(id, out var user) && user.IsActive)
            .GroupBy(id => users[id].Id).ToList();
        var consented = await consents.GetConsentMapForUsersAsync(recipients.Select(group => group.Key).ToList(), cancellationToken);
        var notificationCount = 0;
        foreach (var recipient in recipients)
        {
            var user = users[recipient.First()];
            var outstanding = recipient.SelectMany(id => userDocuments[id])
                .Where(change => !consented.TryGetValue(user.Id, out var versions) || !versions.Contains(change.Version.Id))
                .Select(change => change.Document.Name).Distinct(StringComparer.Ordinal).ToList();
            if (outstanding.Count == 0) continue;
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                logger.LogWarning("Skipping legal document notification batch {BatchId} for user {UserId}: no notification email",
                    batchId, user.Id);
                continue;
            }
            try
            {
                var language = user.PreferredLanguage;
                var message = emailMessages.ReConsentsRequired(user.Id, user.Email, user.BurnerName, outstanding,
                    language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode);
                var deliveryKey = string.Create(CultureInfo.InvariantCulture, $"{message.TemplateName}:{batchId:N}:{user.Id:N}");
                var deliveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryKey)).AsSpan(0, 16));
                await emailService.SendAsync(message with { DeliveryId = deliveryId }, cancellationToken);
                notificationCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Failed to enqueue legal document notification batch {BatchId} for user {UserId}", batchId, user.Id);
            }
        }
        logger.LogInformation("Enqueued legal document batch {BatchId} for {Count} human(s), with {FailureCount} recipient/team failure(s)",
            batchId, notificationCount, failures.Count);
        return failures;
    }
}
