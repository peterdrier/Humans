using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs legal documents from the GitHub repository.
/// </summary>
public class SyncLegalDocumentsJob
{
    private readonly ILegalDocumentSyncService _syncService;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly HumansDbContext _dbContext;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<SyncLegalDocumentsJob> _logger;
    private readonly IClock _clock;

    public SyncLegalDocumentsJob(
        ILegalDocumentSyncService syncService,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        HumansDbContext dbContext,
        HumansMetricsService metrics,
        ILogger<SyncLegalDocumentsJob> logger,
        IClock clock)
    {
        _syncService = syncService;
        _emailService = emailService;
        _membershipCalculator = membershipCalculator;
        _dbContext = dbContext;
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

        // Get active team members for affected teams
        var activeUserIds = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.LeftAt == null && teamIds.Contains(tm.TeamId))
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

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

        var consentsByUser = await _dbContext.ConsentRecords
            .AsNoTracking()
            .Where(cr => activeUserIds.Contains(cr.UserId) && updatedDocVersionIds.Contains(cr.DocumentVersionId))
            .Select(cr => new { cr.UserId, cr.DocumentVersionId })
            .ToListAsync(cancellationToken);

        var userConsents = consentsByUser
            .GroupBy(c => c.UserId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.DocumentVersionId).ToHashSet());

        var usersToNotify = activeUserIds
            .Where(userId => !userConsents.TryGetValue(userId, out var consented) ||
                             !updatedDocVersionIds.All(id => consented.Contains(id)))
            .ToList();

        if (usersToNotify.Count == 0)
        {
            _logger.LogInformation("No users require notifications for these updates");
            return;
        }

        // Batch load user entities for display names and emails
        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => usersToNotify.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var documentNames = updatedDocs.Where(d => d.IsRequired).Select(d => d.Name).ToList();
        var notificationCount = 0;

        foreach (var user in users)
        {
            var effectiveEmail = user.GetEffectiveEmail();
            if (effectiveEmail == null)
            {
                continue;
            }

            await _emailService.SendReConsentsRequiredAsync(
                effectiveEmail,
                user.DisplayName,
                documentNames,
                cancellationToken);

            notificationCount++;
        }

        _logger.LogInformation(
            "Sent consolidated re-consent notifications to {Count} users for documents: {Documents}",
            notificationCount, string.Join(", ", documentNames));
    }
}
