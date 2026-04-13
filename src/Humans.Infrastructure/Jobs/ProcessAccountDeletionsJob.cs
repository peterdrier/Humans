using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Humans.Application.Extensions;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that processes scheduled account deletions.
/// Runs daily to anonymize accounts where the 30-day grace period has expired.
/// </summary>
public class ProcessAccountDeletionsJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IMemoryCache _cache;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<ProcessAccountDeletionsJob> _logger;
    private readonly IClock _clock;

    public ProcessAccountDeletionsJob(
        HumansDbContext dbContext,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IProfileService profileService,
        ITeamService teamService,
        IMemoryCache cache,
        IHumansMetrics metrics,
        ILogger<ProcessAccountDeletionsJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _profileService = profileService;
        _teamService = teamService;
        _cache = cache;
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
            // Find accounts where deletion is scheduled and both the grace period
            // and any event hold (DeletionEligibleAfter) have passed
            var usersToDelete = await _dbContext.Users
                .Include(u => u.Profile)
                .Include(u => u.UserEmails)
                .Include(u => u.TeamMemberships)
                .Include(u => u.RoleAssignments)
                .Where(u => u.DeletionScheduledFor != null && u.DeletionScheduledFor <= now
                    && (u.DeletionEligibleAfter == null || u.DeletionEligibleAfter <= now))
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

            var processedUserIds = new List<Guid>();

            foreach (var user in usersToDelete)
            {
                try
                {
                    // Capture email before anonymization for notification
                    var originalEmail = user.GetEffectiveEmail();
                    var originalName = user.DisplayName;

                    await AnonymizeUserAsync(user, now, cancellationToken);

                    await _auditLogService.LogAsync(
                        AuditAction.AccountAnonymized, nameof(User), user.Id,
                        $"Account anonymized (was {originalName})",
                        nameof(ProcessAccountDeletionsJob));

                    // Save atomically per user so a failure in one doesn't leave others
                    // in a partially-persisted state
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    processedUserIds.Add(user.Id);

                    // Send confirmation to original email if we have it
                    if (!string.IsNullOrEmpty(originalEmail))
                    {
                        await _emailService.SendAccountDeletedAsync(
                            originalEmail,
                            originalName,
                            user.PreferredLanguage,
                            cancellationToken);
                    }

                    _logger.LogInformation(
                        "Successfully anonymized account {UserId}",
                        user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to process deletion for user {UserId}. Reverting tracked changes",
                        user.Id);

                    // Detach only modified/added/deleted entries from the failed user,
                    // keeping remaining unchanged entities tracked for subsequent iterations
                    foreach (var entry in _dbContext.ChangeTracker.Entries().ToList())
                    {
                        if (entry.State != EntityState.Unchanged)
                            entry.State = EntityState.Detached;
                    }
                }
            }

            foreach (var userId in processedUserIds)
            {
                _profileService.UpdateProfileCache(userId, null);
                _cache.InvalidateUserProfile(userId);
                _teamService.RemoveMemberFromAllTeamsCache(userId);
                _cache.InvalidateRoleAssignmentClaims(userId);
                _cache.InvalidateShiftAuthorization(userId);
            }

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
        user.DeletionEligibleAfter = null;

        // Disable login
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        // Anonymize profile if exists
        if (user.Profile is not null)
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
            user.Profile.Pronouns = null;
            user.Profile.DateOfBirth = null;
            user.Profile.ProfilePictureData = null;
            user.Profile.ProfilePictureContentType = null;
            user.Profile.EmergencyContactName = null;
            user.Profile.EmergencyContactPhone = null;
            user.Profile.EmergencyContactRelationship = null;
            user.Profile.ContributionInterests = null;
            user.Profile.BoardNotes = null;
        }

        // Remove contact fields and volunteer history
        if (user.Profile is not null)
        {
            var contactFields = await _dbContext.ContactFields
                .Where(cf => cf.ProfileId == user.Profile.Id)
                .ToListAsync(cancellationToken);
            _dbContext.ContactFields.RemoveRange(contactFields);

            var volunteerHistory = await _dbContext.VolunteerHistoryEntries
                .Where(vh => vh.ProfileId == user.Profile.Id)
                .ToListAsync(cancellationToken);
            _dbContext.VolunteerHistoryEntries.RemoveRange(volunteerHistory);
        }

        // Remove role slot assignments for active memberships
        var activeMemberIds = user.TeamMemberships.Where(m => m.LeftAt is null).Select(m => m.Id).ToList();
        if (activeMemberIds.Count > 0)
        {
            var roleSlotAssignments = await _dbContext.Set<TeamRoleAssignment>()
                .Where(a => activeMemberIds.Contains(a.TeamMemberId))
                .ToListAsync(cancellationToken);
            _dbContext.Set<TeamRoleAssignment>().RemoveRange(roleSlotAssignments);
        }

        // End team memberships (only active ones — may already be ended by RequestDeletion)
        foreach (var membership in user.TeamMemberships.Where(m => m.LeftAt is null))
        {
            membership.LeftAt = now;
        }

        // End role assignments
        foreach (var role in user.RoleAssignments.Where(r => r.ValidTo is null))
        {
            role.ValidTo = now;
        }

        // Cancel active duty signups
        var activeSignups = await _dbContext.ShiftSignups
            .Where(d => d.UserId == user.Id &&
                        (d.Status == SignupStatus.Confirmed || d.Status == SignupStatus.Pending))
            .ToListAsync(cancellationToken);

        foreach (var signup in activeSignups)
        {
            signup.Cancel(_clock, "Account deletion");
            await _auditLogService.LogAsync(
                AuditAction.ShiftSignupCancelled, nameof(ShiftSignup), signup.Id,
                $"Cancelled signup (account deletion) for shift {signup.ShiftId}",
                nameof(ProcessAccountDeletionsJob));
        }

        // Clear iCal token
        user.ICalToken = null;

        // Delete volunteer event profiles
        var eventProfiles = await _dbContext.Set<VolunteerEventProfile>()
            .Where(p => p.UserId == user.Id)
            .ToListAsync(cancellationToken);
        _dbContext.Set<VolunteerEventProfile>().RemoveRange(eventProfiles);

        // Note: We keep consent records and applications for GDPR audit trail
        // These are already anonymized via the user record anonymization
    }
}
