using Humans.Teams.Contracts;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

/// <summary>
/// The body of <c>SyncLegalDocumentsJob</c>, moved inside the section at G5. It named
/// <see cref="LegalDocument"/> and <see cref="IConsentRepository"/> to decide who still
/// owes a signature, and both are internal here — so what crosses the boundary is the
/// call, not the rows (design §15 step 6b).
/// </summary>
internal sealed class LegalDocumentSyncRunner(
    ILegalDocumentSyncService syncService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    IConsentRepository consentRepository,
    ILogger<LegalDocumentSyncRunner> logger) : ILegalDocumentSyncRunner
{
    public async Task SyncAndNotifyAsync(CancellationToken cancellationToken = default)
    {
        var updatedDocs = await syncService.SyncAllDocumentsAsync(cancellationToken);

        if (updatedDocs.Count == 0)
        {
            logger.LogInformation("No legal document updates found");
            return;
        }

        logger.LogInformation(
            "Synced {Count} updated legal documents: {Documents}",
            updatedDocs.Count,
            string.Join(", ", updatedDocs.Select(d => d.Name)));

        await SendReConsentNotificationsAsync(updatedDocs, cancellationToken);
    }

    /// <summary>
    /// Sends re-consent notifications to members who need to consent to updated documents.
    /// Only notifies members of the teams that the updated documents belong to.
    /// </summary>
    private async Task SendReConsentNotificationsAsync(
        IReadOnlyList<LegalDocument> updatedDocs,
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

            await emailService.SendAsync(emailMessages.ReConsentsRequired(
                effectiveEmail,
                user.BurnerName,
                documentNames,
                user.PreferredLanguage),
                cancellationToken);

            notificationCount++;
        }

        logger.LogInformation(
            "Sent consolidated re-consent notifications to {Count} users for documents: {Documents}",
            notificationCount, string.Join(", ", documentNames));
    }
}
