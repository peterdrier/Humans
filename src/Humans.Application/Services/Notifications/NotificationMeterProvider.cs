using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Application-layer implementation of <see cref="INotificationMeterProvider"/>.
/// Provides live counter meters for admin/coordinator work queues. Counts
/// are computed by calling into each owning section service
/// (<see cref="IProfileService"/>, <see cref="IUserService"/>,
/// <see cref="IGoogleSyncService"/>, <see cref="ITeamService"/>,
/// <see cref="ITicketSyncService"/>, <see cref="IApplicationDecisionService"/>)
/// and cached for ~2 minutes. No direct DB access.
/// </summary>
/// <remarks>
/// <para>
/// This service replaces the pre-§15 <c>NotificationMeterProvider</c> that
/// read <c>profiles</c>, <c>users</c>, <c>google_sync_outbox_events</c>,
/// <c>team_join_requests</c>, <c>ticket_sync_states</c>, and
/// <c>applications</c> directly. Per design-rules §2c the Notifications
/// section owns <c>notifications</c>/<c>notification_recipients</c> only —
/// every other table is reached through its owning section's public
/// service interface.
/// </para>
/// <para>
/// The meter counts cache (<see cref="CacheKeys.NotificationMeters"/>) is a
/// short-TTL request-acceleration cache appropriate for <see cref="IMemoryCache"/>
/// per §15i. Writes elsewhere invalidate it via
/// <see cref="INotificationMeterCacheInvalidator"/>.
/// </para>
/// </remarks>
public sealed class NotificationMeterProvider : INotificationMeterProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ITeamService _teamService;
    private readonly ITicketSyncService _ticketSyncService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly ICampService _campService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationMeterProvider> _logger;

    public NotificationMeterProvider(
        IProfileService profileService,
        IUserService userService,
        IGoogleSyncService googleSyncService,
        ITeamService teamService,
        ITicketSyncService ticketSyncService,
        IApplicationDecisionService applicationDecisionService,
        ICampService campService,
        IMemoryCache cache,
        ILogger<NotificationMeterProvider> logger)
    {
        _profileService = profileService;
        _userService = userService;
        _googleSyncService = googleSyncService;
        _teamService = teamService;
        _ticketSyncService = ticketSyncService;
        _applicationDecisionService = applicationDecisionService;
        _campService = campService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NotificationMeter>> GetMetersForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var counts = await GetCachedCountsAsync(cancellationToken);
        var meters = new List<NotificationMeter>();

        var isAdmin = user.IsInRole(RoleNames.Admin);
        var isBoard = user.IsInRole(RoleNames.Board);
        var isVolunteerCoordinator = user.IsInRole(RoleNames.VolunteerCoordinator);
        var isConsentCoordinator = user.IsInRole(RoleNames.ConsentCoordinator);

        // Consent reviews pending — Consent Coordinator
        if (isConsentCoordinator && counts.ConsentReviewsPending > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Consent reviews pending",
                Count = counts.ConsentReviewsPending,
                ActionUrl = "/OnboardingReview",
                Priority = 10,
            });
        }

        // Applications pending board vote — Board (per-user count)
        if (isBoard)
        {
            var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userIdClaim, out var boardMemberUserId))
            {
                var pendingVoteCount = await GetPerUserVotingCountAsync(boardMemberUserId, cancellationToken);
                if (pendingVoteCount > 0)
                {
                    meters.Add(new NotificationMeter
                    {
                        Title = "Applications pending your vote",
                        Count = pendingVoteCount,
                        ActionUrl = "/OnboardingReview/BoardVoting",
                        Priority = 9,
                    });
                }
            }
        }

        // Pending account deletions — Admin
        if (isAdmin && counts.PendingDeletions > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Pending account deletions",
                Count = counts.PendingDeletions,
                ActionUrl = "/Profile/Admin?filter=deleting&sort=name&dir=asc",
                Priority = 8,
            });
        }

        // Failed Google sync events — Admin
        if (isAdmin && counts.FailedSyncEvents > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Failed Google sync events",
                Count = counts.FailedSyncEvents,
                ActionUrl = "/Google/Sync",
                Priority = 7,
            });
        }

        // Onboarding profiles pending — Board / Volunteer Coordinator
        if ((isBoard || isVolunteerCoordinator) && counts.OnboardingPending > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Onboarding profiles pending",
                Count = counts.OnboardingPending,
                ActionUrl = "/OnboardingReview",
                Priority = 6,
            });
        }

        // Team join requests pending — Admin (visible to admins since coordinators
        // see their own team's requests on the team page)
        if (isAdmin && counts.TeamJoinRequestsPending > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Team join requests pending",
                Count = counts.TeamJoinRequestsPending,
                ActionUrl = "/Teams/Summary",
                Priority = 5,
            });
        }

        // Ticket sync error — Admin
        if (isAdmin && counts.TicketSyncError)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Ticket sync error",
                Count = 1,
                ActionUrl = "/Tickets",
                Priority = 4,
            });
        }

        // Pending camp membership requests — per-lead (nobodies-collective#488)
        // Shown as a single counter ("N people want to join your camp") instead of
        // emitting a stored notification per request.
        {
            var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userIdClaim, out var leadUserId))
            {
                var campLeadRequestsPending = await GetPerCampLeadPendingCountAsync(leadUserId, cancellationToken);
                if (campLeadRequestsPending > 0)
                {
                    meters.Add(new NotificationMeter
                    {
                        Title = campLeadRequestsPending == 1
                            ? "1 human wants to join your camp"
                            : $"{campLeadRequestsPending} humans want to join your camp",
                        Count = campLeadRequestsPending,
                        ActionUrl = "/Barrios",
                        Priority = 3,
                    });
                }
            }
        }

        return meters;
    }

    private async Task<int> GetPerCampLeadPendingCountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.CampLeadJoinRequestsBadge(userId);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            try
            {
                return await _campService.GetPendingMembershipCountForLeadAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compute camp lead pending-request count for {UserId}", userId);
                return 0;
            }
        });
    }

    private async Task<MeterCounts> GetCachedCountsAsync(CancellationToken cancellationToken)
    {
        var counts = await _cache.GetOrCreateAsync(CacheKeys.NotificationMeters, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            try
            {
                return await ComputeCountsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compute notification meter counts");
                return new MeterCounts();
            }
        });

        return counts!;
    }

    private async Task<int> GetPerUserVotingCountAsync(Guid boardMemberUserId, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.VotingBadge(boardMemberUserId);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await _applicationDecisionService
                .GetUnvotedApplicationCountAsync(boardMemberUserId, cancellationToken);
        });
    }

    private async Task<MeterCounts> ComputeCountsAsync(CancellationToken cancellationToken)
    {
        var consentReviewsPending = await _profileService.GetConsentReviewPendingCountAsync(cancellationToken);

        var pendingDeletions = await _userService.GetPendingDeletionCountAsync(cancellationToken);

        var failedSyncEvents = await _googleSyncService.GetFailedSyncEventCountAsync(cancellationToken);

        // Onboarding profiles pending excludes consent-review items, matching
        // the board digest "still onboarding" queue semantics.
        var totalNotApproved = await _profileService
            .GetNotApprovedAndNotSuspendedCountAsync(cancellationToken);
        var onboardingPending = totalNotApproved - consentReviewsPending;
        if (onboardingPending < 0)
            onboardingPending = 0;

        var teamJoinRequestsPending = await _teamService
            .GetTotalPendingJoinRequestCountAsync(cancellationToken);

        var ticketSyncError = await _ticketSyncService.IsInErrorStateAsync(cancellationToken);

        return new MeterCounts
        {
            ConsentReviewsPending = consentReviewsPending,
            PendingDeletions = pendingDeletions,
            FailedSyncEvents = failedSyncEvents,
            OnboardingPending = onboardingPending,
            TeamJoinRequestsPending = teamJoinRequestsPending,
            TicketSyncError = ticketSyncError,
        };
    }

    private sealed class MeterCounts
    {
        public int ConsentReviewsPending { get; init; }
        public int PendingDeletions { get; init; }
        public int FailedSyncEvents { get; init; }
        public int OnboardingPending { get; init; }
        public int TeamJoinRequestsPending { get; init; }
        public bool TicketSyncError { get; init; }
    }
}
