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
/// The counts cache is this provider's alone and expires rather than being evicted. Counts the owner already
/// caches or holds in RAM bypass it, so the owner's own eviction keeps them fresh.
/// </summary>
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

        // Admin only: coordinators see their own team's requests on the team page.
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

        // One counter per lead rather than a stored notification per request.
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

    // Not cached here: Camps already holds these reads in RAM (CachingCampService) and
    // drops that projection on its own writes, so a second cache would only add staleness.
    private async Task<int> GetPerCampLeadPendingCountAsync(Guid userId, CancellationToken cancellationToken)
    {
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

    // Governance caches this per user under its own key.
    private Task<int> GetPerUserVotingCountAsync(Guid boardMemberUserId, CancellationToken cancellationToken) =>
        applicationDecisionService.GetUnvotedApplicationCountAsync(boardMemberUserId, cancellationToken);

    private async Task<MeterCounts> ComputeCountsAsync(CancellationToken cancellationToken)
    {
        var allUserInfos = await userService.GetAllUserInfosAsync(cancellationToken).ConfigureAwait(false);
        var reviews = await consentService.GetConsentReviewsAsync(cancellationToken);
        var consentReviewsPending = allUserInfos.Count(u =>
            u.NeedsConsentReview(reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None(u.Id)));

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
