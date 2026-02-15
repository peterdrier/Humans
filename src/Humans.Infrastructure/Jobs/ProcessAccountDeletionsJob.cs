using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that processes scheduled account deletions.
/// Runs daily to anonymize accounts where the 30-day grace period has expired.
/// </summary>
public class ProcessAccountDeletionsJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<ProcessAccountDeletionsJob> _logger;
    private readonly IClock _clock;

    public ProcessAccountDeletionsJob(
        HumansDbContext dbContext,
        IEmailService emailService,
        IAuditLogService auditLogService,
        HumansMetricsService metrics,
        ILogger<ProcessAccountDeletionsJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Processes all accounts scheduled for deletion where the grace period has expired.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        _logger.LogInformation(
            "Starting account deletion processing at {Time}",
            now);

        try
        {
            // Find accounts where deletion is scheduled and the time has passed
            var usersToDelete = await _dbContext.Users
                .Include(u => u.Profile)
                .Include(u => u.UserEmails)
                .Include(u => u.TeamMemberships)
                .Include(u => u.RoleAssignments)
                .Where(u => u.DeletionScheduledFor != null && u.DeletionScheduledFor <= now)
                .ToListAsync(cancellationToken);

            if (usersToDelete.Count == 0)
            {
                _metrics.RecordJobRun("process_account_deletions", "success");
                _logger.LogInformation("No accounts scheduled for deletion");
                return;
            }

            _logger.LogInformation(
                "Found {Count} accounts to process for deletion",
                usersToDelete.Count);

            foreach (var user in usersToDelete)
            {
                try
                {
                    // Capture email before anonymization for notification
                    var originalEmail = user.GetEffectiveEmail();
                    var originalName = user.DisplayName;

                    await AnonymizeUserAsync(user, now, cancellationToken);

                    await _auditLogService.LogAsync(
                        AuditAction.AccountAnonymized, "User", user.Id,
                        $"Account anonymized (was {originalName})",
                        nameof(ProcessAccountDeletionsJob));

                    // Send confirmation to original email if we have it
                    if (!string.IsNullOrEmpty(originalEmail))
                    {
                        await _emailService.SendAccountDeletedAsync(
                            originalEmail,
                            originalName,
                            cancellationToken);
                    }

                    _logger.LogInformation(
                        "Successfully anonymized account {UserId}",
                        user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to process deletion for user {UserId}",
                        user.Id);
                    // Continue processing other users
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _metrics.RecordJobRun("process_account_deletions", "success");
            _logger.LogInformation(
                "Completed account deletion processing, processed {Count} accounts",
                usersToDelete.Count);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("process_account_deletions", "failure");
            _logger.LogError(ex, "Error processing account deletions");
            throw;
        }
    }

    private async Task AnonymizeUserAsync(
        Domain.Entities.User user,
        Instant now,
        CancellationToken cancellationToken)
    {
        // Generate anonymized identifier
        var anonymizedId = $"deleted-{user.Id:N}";

        // Anonymize user record
        user.DisplayName = "Deleted User";
        user.Email = $"{anonymizedId}@deleted.local";
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        user.UserName = anonymizedId;
        user.NormalizedUserName = anonymizedId.ToUpperInvariant();
        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Remove all email addresses
        _dbContext.UserEmails.RemoveRange(user.UserEmails);

        // Clear deletion request fields (deletion is now complete)
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;

        // Disable login
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        // Anonymize profile if exists
        if (user.Profile != null)
        {
            user.Profile.FirstName = "Deleted";
            user.Profile.LastName = "User";
            user.Profile.BurnerName = string.Empty;
            user.Profile.Bio = null;
            user.Profile.City = null;
            user.Profile.CountryCode = null;
            user.Profile.Latitude = null;
            user.Profile.Longitude = null;
            user.Profile.PlaceId = null;
            user.Profile.AdminNotes = null;
        }

        // Remove contact fields
        if (user.Profile != null)
        {
            var contactFields = await _dbContext.ContactFields
                .Where(cf => cf.ProfileId == user.Profile.Id)
                .ToListAsync(cancellationToken);
            _dbContext.ContactFields.RemoveRange(contactFields);
        }

        // End team memberships
        foreach (var membership in user.TeamMemberships)
        {
            membership.LeftAt = now;
        }

        // End role assignments
        foreach (var role in user.RoleAssignments.Where(r => r.ValidTo == null))
        {
            role.ValidTo = now;
        }

        // Note: We keep consent records and applications for GDPR audit trail
        // These are already anonymized via the user record anonymization
    }
}
