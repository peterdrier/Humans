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
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Application-layer implementation of <see cref="INotificationMeterProvider"/>.
/// Provides live counter meters for admin/coordinator work queues. Counts
/// are computed by calling into each owning section service
/// (<see cref="IUserService"/>,
/// <see cref="IGoogleSyncServiceRead"/>, <see cref="ITeamService"/>,
/// <see cref="ITicketSyncService"/>, <see cref="IApplicationDecisionService"/>)
/// and cached for ~2 minutes. No direct DB access.
/// </summary>
/// <remarks>
/// Per design-rules §2c the Notifications section owns
/// <c>notifications</c>/<c>notification_recipients</c> only — every other
/// table is reached through its owning section's public service interface.
/// The meter counts cache (<see cref="CacheKeys.NotificationMeters"/>) is a
/// short-TTL request-acceleration cache appropriate for <see cref="IMemoryCache"/>
/// per §15i. Writes elsewhere invalidate it via
/// <see cref="INotificationMeterCacheInvalidator"/>.
/// </remarks>
public sealed class NotificationMeterProvider(
    IUserServiceRead userService,
    IGoogleSyncServiceRead googleSyncService,
    ITeamServiceRead teamService,
    ITicketSyncService ticketSyncService,
    IApplicationDecisionService applicationDecisionService,
    ICampServiceRead campService,
    IMemoryCache cache,
    ILogger<NotificationMeterProvider> logger) : INotificationMeterProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<NotificationMeter>> GetMetersForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var counts = await GetCachedCountsAsync(cancellationToken);
        var meters = new List<NotificationMeter>();

        var isAdmin = user.IsInRole(RoleNames.Admin);
        var isBoard = user.IsInRole(RoleNames.Board);
        var isVolunteerCoordinator = user.IsInRole(RoleNames.VolunteerCoordinator);
        var isConsentCoordinator = user.IsInRole(RoleNames.ConsentCoordinator);

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
                        ActionUrl = "/Governance/BoardVoting",
                        Priority = 9,
                    });
                }
            }
        }

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
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            try
            {
                var settings = await campService.GetSettingsAsync(cancellationToken);
                var years = settings.OpenSeasons
                    .Append(settings.PublicYear)
                    .Distinct()
                    .OrderBy(year => year);

                var pendingCount = 0;
                foreach (var year in years)
                {
                    var camps = await campService.GetCampsForYearAsync(year, cancellationToken);
                    pendingCount += camps
                        .SelectMany(camp => camp.Seasons)
                        .Where(season =>
                            season.Year == year &&
                            season.Status is CampSeasonStatus.Active or CampSeasonStatus.Full &&
                            season.IsLead(userId))
                        .Sum(season => season.PendingMembers.Count);
                }

                return pendingCount;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to compute camp lead pending-request count for {UserId}", userId);
                return 0;
            }
        });
    }

    private async Task<MeterCounts> GetCachedCountsAsync(CancellationToken cancellationToken)
    {
        var counts = await cache.GetOrCreateAsync(CacheKeys.NotificationMeters, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            try
            {
                return await ComputeCountsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to compute notification meter counts");
                return new MeterCounts();
            }
        });

        return counts!;
    }

    private async Task<int> GetPerUserVotingCountAsync(Guid boardMemberUserId, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.VotingBadge(boardMemberUserId);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await applicationDecisionService
                .GetUnvotedApplicationCountAsync(boardMemberUserId, cancellationToken);
        });
    }

    private async Task<MeterCounts> ComputeCountsAsync(CancellationToken cancellationToken)
    {
        var allUserInfos = await userService.GetAllUserInfosAsync(cancellationToken).ConfigureAwait(false);
        var consentReviewsPending = allUserInfos.Count(u => u.NeedsConsentReview);

        // Pending deletions derived from the cached UserInfo snapshot —
        // ConsentReviewsPending above already reads the same snapshot, and the
        // meter result itself is cached for CacheDuration anyway.
        var pendingDeletions = allUserInfos
            .Count(u => u.DeletionRequestedAt != null);

        var failedSyncEvents = await googleSyncService.GetFailedSyncEventCountAsync(cancellationToken);

        // Board / VolunteerCoordinator see the same review queue under a
        // different label — same predicate as consentReviewsPending.
        var onboardingPending = consentReviewsPending;

        var teamJoinRequestsPending = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Sum(t => t.PendingRequestCount);

        var ticketSyncError = await ticketSyncService.IsInErrorStateAsync(cancellationToken);

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
