using System.Security.Claims;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Provides live counter meters for admin/coordinator work queues.
/// Counts are computed from DB and cached ~2 minutes. No DB storage or read state.
/// </summary>
public class NotificationMeterProvider : INotificationMeterProvider
{
    private readonly HumansDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NotificationMeterProvider> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);


    public NotificationMeterProvider(
        HumansDbContext dbContext,
        IMemoryCache cache,
        ILogger<NotificationMeterProvider> logger)
    {
        _dbContext = dbContext;
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

        // Applications pending board vote — Board
        if (isBoard && counts.ApplicationsPendingVote > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Applications pending board vote",
                Count = counts.ApplicationsPendingVote,
                ActionUrl = "/OnboardingReview/BoardVoting",
                Priority = 9,
            });
        }

        // Pending account deletions — Admin
        if (isAdmin && counts.PendingDeletions > 0)
        {
            meters.Add(new NotificationMeter
            {
                Title = "Pending account deletions",
                Count = counts.PendingDeletions,
                ActionUrl = "/Admin",
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

        return meters;
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

    private async Task<MeterCounts> ComputeCountsAsync(CancellationToken cancellationToken)
    {
        // Consent reviews pending (Pending or Flagged, not rejected)
        var consentReviewsPending = await _dbContext.Profiles
            .CountAsync(p => p.ConsentCheckStatus != null
                && (p.ConsentCheckStatus == ConsentCheckStatus.Pending
                    || p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
                && p.RejectedAt == null, cancellationToken);

        // Applications pending board vote
        var applicationsPendingVote = await _dbContext.Applications
            .CountAsync(a => a.Status == ApplicationStatus.Submitted, cancellationToken);

        // Pending account deletions
        var pendingDeletions = await _dbContext.Users
            .CountAsync(u => u.DeletionRequestedAt != null, cancellationToken);

        // Failed Google sync events
        var failedSyncEvents = await _dbContext.GoogleSyncOutboxEvents
            .CountAsync(e => e.ProcessedAt == null && e.LastError != null, cancellationToken);

        // Onboarding profiles pending excludes consent-review items, matching the
        // existing board digest "still onboarding" queue semantics.
        var totalNotApproved = await _dbContext.Profiles
            .CountAsync(p => !p.IsApproved && !p.IsSuspended, cancellationToken);
        var onboardingPending = totalNotApproved - consentReviewsPending;
        if (onboardingPending < 0)
            onboardingPending = 0;

        // Team join requests pending
        var teamJoinRequestsPending = await _dbContext.TeamJoinRequests
            .CountAsync(r => r.Status == TeamJoinRequestStatus.Pending, cancellationToken);

        // Ticket sync error
        var ticketSyncState = await _dbContext.TicketSyncStates.FindAsync([1], cancellationToken);
        var ticketSyncError = ticketSyncState?.SyncStatus == TicketSyncStatus.Error;

        return new MeterCounts
        {
            ConsentReviewsPending = consentReviewsPending,
            ApplicationsPendingVote = applicationsPendingVote,
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
        public int ApplicationsPendingVote { get; init; }
        public int PendingDeletions { get; init; }
        public int FailedSyncEvents { get; init; }
        public int OnboardingPending { get; init; }
        public int TeamJoinRequestsPending { get; init; }
        public bool TicketSyncError { get; init; }
    }
}
