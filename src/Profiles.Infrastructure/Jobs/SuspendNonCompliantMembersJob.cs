using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
public class SuspendNonCompliantMembersJob
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ILogger<SuspendNonCompliantMembersJob> _logger;
    private readonly IClock _clock;

    public SuspendNonCompliantMembersJob(
        ProfilesDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        IEmailService emailService,
        IGoogleSyncService googleSyncService,
        ILogger<SuspendNonCompliantMembersJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _emailService = emailService;
        _googleSyncService = googleSyncService;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Checks and updates membership status for users missing required consents past grace period.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting non-compliant member suspension check at {Time}",
            _clock.GetCurrentInstant());

        try
        {
            // Get users who are now Inactive (missing consents + grace period expired)
            var usersToSuspend = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            if (usersToSuspend.Count == 0)
            {
                _logger.LogInformation("Completed suspension check, no users require suspension");
                return;
            }

            // Batch load all users with profiles and their team memberships (tracked for persistence)
            var users = await _dbContext.Users
                .Include(u => u.Profile)
                .Include(u => u.TeamMemberships.Where(tm => tm.LeftAt == null))
                .Where(u => usersToSuspend.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var now = _clock.GetCurrentInstant();
            var suspendedCount = 0;

            foreach (var user in users)
            {
                if (user.Profile == null)
                {
                    continue;
                }

                // 1. Set suspended flag on profile
                user.Profile.IsSuspended = true;
                user.Profile.UpdatedAt = now;

                // 2. Send notification
                var effectiveEmail = user.GetEffectiveEmail();
                if (effectiveEmail != null)
                {
                    await _emailService.SendAccessSuspendedAsync(
                        effectiveEmail,
                        user.DisplayName,
                        "Missing required document consent (grace period expired)",
                        cancellationToken);
                }

                // 3. Remove from all team resources (Google Drive/Groups)
                foreach (var membership in user.TeamMemberships)
                {
                    try
                    {
                        await _googleSyncService.RemoveUserFromTeamResourcesAsync(
                            membership.TeamId,
                            user.Id,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove user {UserId} from team {TeamId} resources during suspension",
                            user.Id, membership.TeamId);
                    }
                }

                _logger.LogWarning(
                    "User {UserId} ({Email}) suspended and removed from {Count} teams",
                    user.Id, effectiveEmail, user.TeamMemberships.Count);

                suspendedCount++;
            }

            if (suspendedCount > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Completed non-compliant member check, suspended {Count} members",
                suspendedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking non-compliant members");
            throw;
        }
    }
}
