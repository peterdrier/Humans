using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs legal documents from the GitHub repository.
/// </summary>
/// <remarks>
/// Reads active team member user ids via <see cref="ITeamService"/> and user
/// display data via <see cref="IUserService"/> so the job never touches
/// <see cref="Humans.Infrastructure.Data.HumansDbContext"/> directly
/// (design-rules §2c). Consent lookups remain on <see cref="IConsentRepository"/>
/// which is already the Legal &amp; Consent section's owned repository.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SyncLegalDocumentsJob(
    ILegalDocumentSyncService syncService,
    IEmailService emailService,
    ITeamService teamService,
    IUserService userService,
    IConsentRepository consentRepository,
    IHumansMetrics metrics,
    ILogger<SyncLegalDocumentsJob> logger,
    IClock clock) : IRecurringJob
{
    /// <summary>
    /// Executes the legal document sync job.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting legal document sync at {Time}", clock.GetCurrentInstant());

        try
        {
            var updatedDocs = await syncService.SyncAllDocumentsAsync(cancellationToken);

            if (updatedDocs.Count > 0)
            {
                logger.LogInformation(
                    "Synced {Count} updated legal documents: {Documents}",
                    updatedDocs.Count,
                    string.Join(", ", updatedDocs.Select(d => d.Name)));

                // Send re-consent notifications to affected members
                await SendReConsentNotificationsAsync(updatedDocs, cancellationToken);
            }
            else
            {
                logger.LogInformation("No legal document updates found");
            }

            metrics.RecordJobRun("sync_legal_documents", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("sync_legal_documents", "failure");
            logger.LogError(ex, "Error syncing legal documents");
            throw;
        }
    }

    /// <summary>
    /// Sends re-consent notifications to members who need to consent to updated documents.
    /// Only notifies members of the teams that the updated documents belong to.
    /// </summary>
    private async Task SendReConsentNotificationsAsync(
        IReadOnlyList<Domain.Entities.LegalDocument> updatedDocs,
        CancellationToken cancellationToken)
    {
        // Get unique team IDs for updated docs
        var teamIds = updatedDocs.Select(d => d.TeamId).Distinct().ToList();

        // Get active team members for affected teams (union across teams, de-duped).
        var activeUserIds = new HashSet<Guid>();
        foreach (var teamId in teamIds)
        {
            var team = await teamService.GetTeamAsync(teamId, cancellationToken);
            if (team is null)
                continue;

            foreach (var userId in team.Members.Select(m => m.UserId))
            {
                activeUserIds.Add(userId);
            }
        }

        if (activeUserIds.Count == 0)
        {
            logger.LogInformation("No team members to notify for re-consent");
            return;
        }

        // Filter to users who actually need to sign THESE updated documents
        // We check if they have consented to the LATEST version of each updated doc
        var updatedDocVersionIds = updatedDocs
            .Select(d => d.Versions.OrderByDescending(v => v.EffectiveFrom).First().Id)
            .ToList();

        var activeUserIdList = activeUserIds.ToList();
        var consentPairs = await consentRepository.GetPairsForUsersAndVersionsAsync(
            activeUserIdList, updatedDocVersionIds, cancellationToken);

        var userConsents = consentPairs
            .GroupBy(c => c.UserId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.DocumentVersionId).ToHashSet());

        var usersToNotify = activeUserIdList
            .Where(userId => !userConsents.TryGetValue(userId, out var consented) ||
                             !updatedDocVersionIds.All(id => consented.Contains(id)))
            .ToList();

        if (usersToNotify.Count == 0)
        {
            logger.LogInformation("No users require notifications for these updates");
            return;
        }

        // Batch load UserInfo snapshots via IUserService so we resolve the
        // verified notification-target address (UserInfo.Email mirrors
        // User.GetEffectiveEmail).
        var users = await userService.GetUserInfosAsync(usersToNotify, cancellationToken);

        var documentNames = updatedDocs.Where(d => d.IsRequired).Select(d => d.Name).ToList();
        var notificationCount = 0;

        foreach (var userId in usersToNotify)
        {
            if (!users.TryGetValue(userId, out var user))
            {
                continue;
            }

            var effectiveEmail = user.Email;
            if (effectiveEmail is null)
            {
                continue;
            }

            await emailService.SendReConsentsRequiredAsync(
                effectiveEmail,
                user.BurnerName,
                documentNames,
                user.PreferredLanguage,
                cancellationToken);

            notificationCount++;
        }

        logger.LogInformation(
            "Sent consolidated re-consent notifications to {Count} users for documents: {Documents}",
            notificationCount, string.Join(", ", documentNames));
    }
}
