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
            var usersToUpdate = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            var suspendedCount = 0;

            foreach (var userId in usersToUpdate)
            {
                var status = await _membershipCalculator.ComputeStatusAsync(userId, cancellationToken);

                if (status == MembershipStatus.Inactive)
                {
                    var user = await _dbContext.Users
                        .Include(u => u.Profile)
                        .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

                    if (user?.Email != null)
                    {
                        await _emailService.SendAccessSuspendedAsync(
                            user.Email,
                            user.DisplayName,
                            "Missing required document consent",
                            cancellationToken);

                        _logger.LogWarning(
                            "User {UserId} ({Email}) access suspended due to missing consent",
                            userId, user.Email);

                        suspendedCount++;
                    }
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
