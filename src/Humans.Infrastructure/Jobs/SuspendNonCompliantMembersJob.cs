using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Metering;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
/// <remarks>
/// All reads/writes fan out through section services
/// (<see cref="IUserService"/>, <see cref="IProfileService"/>,
/// <see cref="ITeamService"/>, <see cref="IGoogleSyncService"/>) so the job
/// never touches <see cref="Humans.Infrastructure.Data.HumansDbContext"/>
/// directly (design-rules Â§2c). Cross-cutting cache invalidation routes
/// through invalidator interfaces
/// (<see cref="IFullProfileInvalidator"/>,
/// <see cref="IRoleAssignmentClaimsCacheInvalidator"/>,
/// <see cref="IShiftAuthorizationInvalidator"/>) rather than IMemoryCache.
/// </remarks>
public class SuspendNonCompliantMembersJob : IRecurringJob
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly Counter<long> _jobRunsCounter;
    private readonly Counter<long> _membersSuspendedCounter;
    private readonly ILogger<SuspendNonCompliantMembersJob> _logger;
    private readonly IClock _clock;

    public SuspendNonCompliantMembersJob(
        IUserService userService,
        IProfileService profileService,
        ITeamService teamService,
        IMembershipCalculator membershipCalculator,
        IEmailService emailService,
        INotificationService notificationService,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        IFullProfileInvalidator fullProfileInvalidator,
        IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
        IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
        IMeters meters,
        ILogger<SuspendNonCompliantMembersJob> logger,
        IClock clock)
    {
        _userService = userService;
        _profileService = profileService;
        _teamService = teamService;
        _membershipCalculator = membershipCalculator;
        _emailService = emailService;
        _notificationService = notificationService;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _fullProfileInvalidator = fullProfileInvalidator;
        _roleAssignmentClaimsInvalidator = roleAssignmentClaimsInvalidator;
        _shiftAuthorizationInvalidator = shiftAuthorizationInvalidator;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
        _membersSuspendedCounter = meters.RegisterCounter(
            "humans.members_suspended_total",
            new MeterMetadata("Total member suspensions", "{members}"));
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

            var now = _clock.GetCurrentInstant();

            // Apply the suspension write through IProfileService â€” returns the
            // subset of user ids whose profile was actually mutated (skips
            // already-suspended / profileless users).
            var suspendedIds = await _profileService
                .SuspendForMissingConsentAsync(usersToSuspend, now, cancellationToken);

            if (suspendedIds.Count == 0)
            {
                _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "suspend_noncompliant_members"),
                new KeyValuePair<string, object?>("result", "success"));
                _logger.LogInformation(
                    "Completed non-compliant member check, no eligible users to suspend");
                return;
            }

            // Fan out user + email hydration for notifications, and team membership
            // lookup for Google-sync cleanup.
            var usersById = await _userService
                .GetByIdsWithEmailsAsync(suspendedIds, cancellationToken);

            foreach (var userId in suspendedIds)
            {
                if (!usersById.TryGetValue(userId, out var user))
                {
                    _logger.LogWarning(
                        "Suspended user {UserId} not found in user lookup â€” skipping downstream side effects",
                        userId);
                    continue;
                }

                // 1. Send email notification
                var effectiveEmail = user.GetEffectiveEmail();
                if (effectiveEmail is not null)
                {
                    try
                    {
                        await _emailService.SendAccessSuspendedAsync(
                            effectiveEmail,
                            user.DisplayName,
                            "Missing required document consent (grace period expired)",
                            user.PreferredLanguage,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send suspension email for user {UserId}", user.Id);
                    }
                }

                // 2. Send in-app notification (best-effort)
                try
                {
                    await _notificationService.SendAsync(
                        NotificationSource.AccessSuspended,
                        NotificationClass.Actionable,
                        NotificationPriority.Critical,
                        "Your access has been suspended",
                        [user.Id],
                        body: "Your access has been suspended because required document consent is missing. Please review and sign the required documents to restore access.",
                        actionUrl: "/Legal/Consent",
                        actionLabel: "Review documents",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", user.Id);
                }

                // 3. Remove from all team resources (Google Drive/Groups) via ITeamService lookup.
                var memberships = await _teamService.GetUserTeamsAsync(user.Id, cancellationToken);
                foreach (var membership in memberships)
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
                    "User {UserId} ({Email}) suspended and flagged for removal from {Count} teams",
                    user.Id, effectiveEmail, memberships.Count);

                _membersSuspendedCounter.Add(1, new KeyValuePair<string, object?>("source", "job"));

                // 4. Audit log + cross-cutting cache invalidation.
                await _auditLogService.LogAsync(
                    AuditAction.MemberSuspended, nameof(User), user.Id,
                    $"{user.DisplayName} suspended for missing required document consent (grace period expired)",
                    nameof(SuspendNonCompliantMembersJob));

                await _fullProfileInvalidator.InvalidateAsync(user.Id, cancellationToken);
                _roleAssignmentClaimsInvalidator.Invalidate(user.Id);
                _shiftAuthorizationInvalidator.Invalidate(user.Id);
                _teamService.RemoveMemberFromAllTeamsCache(user.Id);
            }

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "suspend_noncompliant_members"),
                new KeyValuePair<string, object?>("result", "success"));
            _logger.LogInformation(
                "Completed non-compliant member check, suspended {Count} members",
                suspendedIds.Count);
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "suspend_noncompliant_members"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error checking non-compliant members");
            throw;
        }
    }
}
