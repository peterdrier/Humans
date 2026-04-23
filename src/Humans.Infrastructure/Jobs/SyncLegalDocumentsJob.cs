using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
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
public class SyncLegalDocumentsJob : IRecurringJob
{
    private readonly ILegalDocumentSyncService _syncService;
    private readonly IEmailService _emailService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IConsentRepository _consentRepository;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<SyncLegalDocumentsJob> _logger;
    private readonly IClock _clock;

    public SyncLegalDocumentsJob(
        ILegalDocumentSyncService syncService,
        IEmailService emailService,
        ITeamService teamService,
        IUserService userService,
        IConsentRepository consentRepository,
        IHumansMetrics metrics,
        ILogger<SyncLegalDocumentsJob> logger,
        IClock clock)
    {
        _syncService = syncService;
        _emailService = emailService;
        _teamService = teamService;
        _userService = userService;
        _consentRepository = consentRepository;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Executes the legal document sync job.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting legal document sync at {Time}", _clock.GetCurrentInstant());

        try
        {
            var updatedDocs = await _syncService.SyncAllDocumentsAsync(cancellationToken);

            if (updatedDocs.Count > 0)
            {
                _logger.LogInformation(
                    "Synced {Count} updated legal documents: {Documents}",
                    updatedDocs.Count,
                    string.Join(", ", updatedDocs.Select(d => d.Name)));

                // Send re-consent notifications to affected members
                await SendReConsentNotificationsAsync(updatedDocs, cancellationToken);
            }
            else
            {
                _logger.LogInformation("No legal document updates found");
            }

            _metrics.RecordJobRun("sync_legal_documents", "success");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("sync_legal_documents", "failure");
            _logger.LogError(ex, "Error syncing legal documents");
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
            var members = await _teamService.GetActiveMemberUserIdsAsync(teamId, cancellationToken);
            foreach (var userId in members)
            {
                activeUserIds.Add(userId);
            }
        }

        if (activeUserIds.Count == 0)
        {
            _logger.LogInformation("No team members to notify for re-consent");
            return;
        }

        // Filter to users who actually need to sign THESE updated documents
        // We check if they have consented to the LATEST version of each updated doc
        var updatedDocVersionIds = updatedDocs
            .Select(d => d.Versions.OrderByDescending(v => v.EffectiveFrom).First().Id)
            .ToList();

        var activeUserIdList = activeUserIds.ToList();
        var consentPairs = await _consentRepository.GetPairsForUsersAndVersionsAsync(
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
            _logger.LogInformation("No users require notifications for these updates");
            return;
        }

        // Batch load user entities for display names and emails via
        // IUserService. Use the -WithEmails variant so GetEffectiveEmail
        // picks the verified notification-target address.
        var users = await _userService.GetByIdsWithEmailsAsync(usersToNotify, cancellationToken);

        var documentNames = updatedDocs.Where(d => d.IsRequired).Select(d => d.Name).ToList();
        var notificationCount = 0;

        foreach (var userId in usersToNotify)
        {
            if (!users.TryGetValue(userId, out var user))
            {
                continue;
            }

            var effectiveEmail = user.GetEffectiveEmail();
            if (effectiveEmail is null)
            {
                continue;
            }

            await _emailService.SendReConsentsRequiredAsync(
                effectiveEmail,
                user.DisplayName,
                documentNames,
                user.PreferredLanguage,
                cancellationToken);

            notificationCount++;
        }

        _logger.LogInformation(
            "Sent consolidated re-consent notifications to {Count} users for documents: {Documents}",
            notificationCount, string.Join(", ", documentNames));
    }
}
