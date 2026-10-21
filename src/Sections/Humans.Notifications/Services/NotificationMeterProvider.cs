using Humans.Consent.Contracts;
using Microsoft.Extensions.Localization;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Caching;
using System.Security.Claims;
using Humans.Notifications.Services.Dtos;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.Teams.Contracts;

using Humans.Base.Interfaces;
using Humans.Base.Constants;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Humans.Users.Contracts;

namespace Humans.Notifications.Services;

/// <summary>
/// Provides live counter meters for admin/coordinator work queues. Counts
/// are computed by calling into each owning section's read interface
/// (<see cref="IUserService"/>, <see cref="IGoogleSyncService"/>,
/// <see cref="ITeamService"/>, <see cref="ITicketSync"/>,
/// <see cref="IGovernanceService"/>, <see cref="ICampService"/>)
/// and cached for ~2 minutes. No direct DB access.
/// </summary>
/// <remarks>
/// Notifications owns <c>notifications</c>/<c>notification_recipients</c> only —
/// every other table is reached through its owning section's public service
/// interface. The meter counts cache (<see cref="CountsCacheKey"/>) is a short-TTL
/// request-acceleration cache this provider alone reads; nothing evicts it, it
/// expires. Counts their owner already caches or holds in RAM (team join requests,
/// per-member voting) bypass it, so the owner's own eviction keeps them fresh.
/// </remarks>
internal sealed class NotificationMeterProvider(
    IUserService userService,
    IConsentService consentService,
    IGoogleSyncService googleSyncService,
    ITeamService teamService,
    ITicketSync ticketSyncService,
    IGovernanceService applicationDecisionService,
    ICampService campService,
    IMemoryCache cache,
    ILogger<NotificationMeterProvider> logger,
    IStringLocalizer<NotificationsResource> localizer) : IOrchestrator
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>Cache key for the shared meter counts; this provider is its only reader and writer.</summary>
    internal const string CountsCacheKey = "NotificationMeters";

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
                Title = localizer["Notifications_Meter_ConsentReviewsPending"],
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
                        Title = localizer["Notifications_Meter_ApplicationsPendingVote"],
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
                Title = localizer["Notifications_Meter_PendingAccountDeletions"],
                Count = counts.PendingDeletions,
                ActionUrl = "/Users/Admin?filter=deleting&sort=name&dir=asc",
                Priority = 8,
            });
        }

        if (isAdmin && counts.FailedSyncEvents > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = localizer["Notifications_Meter_FailedGoogleSyncEvents"],
                Count = counts.FailedSyncEvents,
                ActionUrl = "/Google/SyncOutbox",
                Priority = 7,
            });
        }

        // Board / VolunteerCoordinator see the same review queue the Consent Coordinator
        // sees, under a label that matches how they think of it.
        if ((isBoard || isVolunteerCoordinator) && counts.ConsentReviewsPending > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = localizer["Notifications_Meter_OnboardingProfilesPending"],
                Count = counts.ConsentReviewsPending,
                ActionUrl = "/OnboardingReview",
                Priority = 6,
            });
        }

        // Team join requests pending — Admin (visible to admins since coordinators
        // see their own team's requests on the team page)
        var teamJoinRequestsPending = isAdmin ? await GetTeamJoinRequestsPendingAsync(cancellationToken) : 0;
        if (teamJoinRequestsPending > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = localizer["Notifications_Meter_TeamJoinRequestsPending"],
                Count = teamJoinRequestsPending,
                ActionUrl = "/Teams/Summary",
                Priority = 5,
            });
        }

        if (isAdmin && counts.TicketSyncError)
        {
            meters.Add(new NotificationMeter
            {
                Title = localizer["Notifications_Meter_TicketSyncError"],
                Count = 1,
                ActionUrl = "/Tickets",
                Priority = 4,
            });
        }

        // Pending camp membership requests, per lead: one counter rather than a stored
        // notification per request.
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
                            ? localizer["Notifications_Meter_CampJoinRequest"]
                            : localizer["Notifications_Meter_CampJoinRequests", campLeadRequestsPending],
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        var counts = await cache.GetOrCreateAsync(CountsCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            try
            {
                return await ComputeCountsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to compute notification meter counts");
                return new MeterCounts();
            }
        });

        return counts!;
    }

    // Read live: Teams' CachingTeamService holds every team in RAM and drops it on its own
    // join-request writes, so a second cache here would only add staleness.
    private async Task<int> GetTeamJoinRequestsPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await teamService.GetTeamsAsync(cancellationToken)).Values
                .Sum(t => t.PendingRequestCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compute team join request meter count");
            return 0;
        }
    }

    // Caching lives in ApplicationDecisionService.GetUnvotedApplicationCountAsync (CacheKeys.VotingBadge).
    private Task<int> GetPerUserVotingCountAsync(Guid boardMemberUserId, CancellationToken cancellationToken) =>
        applicationDecisionService.GetUnvotedApplicationCountAsync(boardMemberUserId, cancellationToken);

    private async Task<MeterCounts> ComputeCountsAsync(CancellationToken cancellationToken)
    {
        var allUserInfos = await userService.GetAllUserInfosAsync(cancellationToken).ConfigureAwait(false);
        var reviews = await consentService.GetConsentReviewsAsync(cancellationToken);
        var consentReviewsPending = allUserInfos.Count(u =>
            u.NeedsConsentReview(reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None(u.Id)));

        // Pending deletions derived from the cached UserInfo snapshot —
        // ConsentReviewsPending above already reads the same snapshot, and the
        // meter result itself is cached for CacheDuration anyway.
        var pendingDeletions = allUserInfos
            .Count(u => u.DeletionRequestedAt != null);

        var failedSyncEvents = await googleSyncService.GetFailedSyncEventCountAsync(cancellationToken);

        var ticketSyncError = await ticketSyncService.IsInErrorStateAsync(cancellationToken);

        return new MeterCounts
        {
            ConsentReviewsPending = consentReviewsPending,
            PendingDeletions = pendingDeletions,
            FailedSyncEvents = failedSyncEvents,
            TicketSyncError = ticketSyncError,
        };
    }

    private sealed class MeterCounts
    {
        public int ConsentReviewsPending { get; init; }
        public int PendingDeletions { get; init; }
        public int FailedSyncEvents { get; init; }
        public bool TicketSyncError { get; init; }
    }
}
