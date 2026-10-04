using Humans.Base.Extensions;
using Humans.Teams.Contracts;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

/// <summary>
/// The body of <c>SyncLegalDocumentsJob</c>. It names <see cref="LegalDocument"/> and
/// <see cref="IConsentRepository"/> to decide who still owes a signature, and both are
/// internal here — so what crosses the boundary is the call, not the rows
/// (design §15 step 6b).
/// </summary>
internal sealed class LegalDocumentSyncRunner(
    ILegalDocumentSyncService syncService,
    IEmailService emailService,
    ConsentEmails emailMessages,
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
        var requiredDocs = updatedDocs.Where(d => d.IsRequired).ToList();
        var userDocuments = new Dictionary<Guid, List<LegalDocument>>();
        foreach (var teamDocuments in requiredDocs.GroupBy(d => d.TeamId))
        {
            var team = await teamService.GetTeamAsync(teamDocuments.Key, cancellationToken);
            if (team is null)
                continue;

            foreach (var userId in team.Members.Select(m => m.UserId).Distinct())
            {
                if (!userDocuments.TryGetValue(userId, out var documents))
                {
                    documents = [];
                    userDocuments.Add(userId, documents);
                }
                documents.AddRange(teamDocuments);
            }
        }

        if (userDocuments.Count == 0)
        {
            logger.LogInformation("No team members to notify for re-consent");
            return;
        }

        // Consent check runs against the LATEST version of each updated doc.
        var updatedDocVersionIds = requiredDocs
            .Select(d => d.Versions.OrderByDescending(v => v.EffectiveFrom).First().Id)
            .ToList();

        var activeUserIdList = userDocuments.Keys.ToList();
        var consentPairs = await consentRepository.GetPairsForUsersAndVersionsAsync(
            activeUserIdList, updatedDocVersionIds, cancellationToken);

        var userConsents = consentPairs
            .GroupBy(c => c.UserId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.DocumentVersionId).ToHashSet());

        var outstandingDocuments = userDocuments.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Where(d =>
                !userConsents.TryGetValue(entry.Key, out var consented) ||
                !consented.Contains(d.CurrentVersion!.Id)).ToList());
        var usersToNotify = outstandingDocuments.Where(entry => entry.Value.Count > 0)
            .Select(entry => entry.Key).ToList();

        if (usersToNotify.Count == 0)
        {
            logger.LogInformation("No users require notifications for these updates");
            return;
        }

        // Batch load UserInfo snapshots via IUserService so we resolve the
        // verified notification-target address (UserInfo.Email mirrors
        // User.GetEffectiveEmail).
        var users = await userService.GetUserInfosAsync(usersToNotify, cancellationToken);

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

            var language = user.PreferredLanguage;
            await emailService.SendAsync(emailMessages.ReConsentsRequired(
                effectiveEmail,
                user.BurnerName,
                outstandingDocuments[userId].Select(d => d.Name).ToList(),
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode),
                cancellationToken);

            notificationCount++;
        }

        logger.LogInformation(
            "Sent consolidated re-consent notifications to {Count} users for documents: {Documents}",
            notificationCount, string.Join(", ", requiredDocs.Select(d => d.Name)));
    }
}
