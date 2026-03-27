namespace Humans.Application.DTOs;

/// <summary>
/// Counts for the Admin daily digest email — system health and operational items.
/// </summary>
public record AdminDigestCounts(
    int PendingDeletions,
    int PendingConsents,
    int TeamJoinRequests,
    int OnboardingReview,
    int StillOnboarding,
    int BoardVotingTotal,
    int FailedSyncOutboxEvents,
    bool TicketSyncError,
    string? TicketSyncErrorMessage);
