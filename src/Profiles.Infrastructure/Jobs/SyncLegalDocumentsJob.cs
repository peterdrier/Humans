using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs legal documents from the GitHub repository.
/// </summary>
public class SyncLegalDocumentsJob
{
    private readonly ILegalDocumentSyncService _syncService;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly ProfilesDbContext _dbContext;
    private readonly ILogger<SyncLegalDocumentsJob> _logger;
    private readonly IClock _clock;

    public SyncLegalDocumentsJob(
        ILegalDocumentSyncService syncService,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        ProfilesDbContext dbContext,
        ILogger<SyncLegalDocumentsJob> logger,
        IClock clock)
    {
        _syncService = syncService;
        _emailService = emailService;
        _membershipCalculator = membershipCalculator;
        _dbContext = dbContext;
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing legal documents");
            throw;
        }
    }

    /// <summary>
    /// Sends re-consent notifications to members who need to consent to updated documents.
    /// </summary>
    private async Task SendReConsentNotificationsAsync(
        IReadOnlyList<Domain.Entities.LegalDocument> updatedDocs,
        CancellationToken cancellationToken)
    {
        // Get users who need to re-consent
        var usersNeedingReConsent = await _membershipCalculator
            .GetUsersRequiringStatusUpdateAsync(cancellationToken);

        if (usersNeedingReConsent.Count == 0)
        {
            _logger.LogInformation("No users require re-consent notifications");
            return;
        }

        var documentNames = string.Join(", ", updatedDocs.Select(d => d.Name));
        var notificationCount = 0;

        foreach (var userId in usersNeedingReConsent)
        {
            var user = await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            var effectiveEmail = user?.GetEffectiveEmail();
            if (effectiveEmail == null)
            {
                continue;
            }

            // Send notification for each updated document that requires re-consent
            foreach (var doc in updatedDocs.Where(d => d.IsRequired))
            {
                await _emailService.SendReConsentRequiredAsync(
                    effectiveEmail,
                    user!.DisplayName,
                    doc.Name,
                    cancellationToken);
            }

            notificationCount++;
        }

        _logger.LogInformation(
            "Sent re-consent notifications to {Count} users for documents: {Documents}",
            notificationCount, documentNames);
    }
}
