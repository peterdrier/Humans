using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents.
/// </summary>
public class SuspendNonCompliantMembersJob
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly ILogger<SuspendNonCompliantMembersJob> _logger;
    private readonly IClock _clock;

    public SuspendNonCompliantMembersJob(
        ProfilesDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        IEmailService emailService,
        ILogger<SuspendNonCompliantMembersJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _emailService = emailService;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Checks and updates membership status for users missing required consents.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting non-compliant member check at {Time}",
            _clock.GetCurrentInstant());

        try
        {
            // GetUsersRequiringStatusUpdateAsync already identifies users missing consents
            // who have active roles - these users will have Inactive status
            var usersToUpdate = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            if (usersToUpdate.Count == 0)
            {
                _logger.LogInformation("Completed non-compliant member check, no users require update");
                return;
            }

            // Batch load all users with profiles to avoid N+1
            var users = await _dbContext.Users
                .AsNoTracking()
                .Include(u => u.Profile)
                .Where(u => usersToUpdate.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var userLookup = users.ToDictionary(u => u.Id);
            var suspendedCount = 0;

            foreach (var userId in usersToUpdate)
            {
                // GetUsersRequiringStatusUpdateAsync already verified these users are missing consents
                // and have active roles, so they will compute as Inactive
                if (!userLookup.TryGetValue(userId, out var user))
                {
                    continue;
                }

                var effectiveEmail = user.GetEffectiveEmail();
                if (effectiveEmail != null)
                {
                    await _emailService.SendAccessSuspendedAsync(
                        effectiveEmail,
                        user.DisplayName,
                        "Missing required document consent",
                        cancellationToken);

                    _logger.LogWarning(
                        "User {UserId} ({Email}) access suspended due to missing consent",
                        userId, effectiveEmail);

                    suspendedCount++;
                }
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
