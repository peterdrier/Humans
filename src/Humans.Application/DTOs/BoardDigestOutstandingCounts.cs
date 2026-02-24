namespace Humans.Application.DTOs;

/// <summary>
/// Outstanding item counts for the Board daily digest email.
/// BoardVotingYours is personalized per board member; all others are shared.
/// </summary>
public record BoardDigestOutstandingCounts(
    int OnboardingReview,
    int StillOnboarding,
    int BoardVotingTotal,
    int BoardVotingYours,
    int TeamJoinRequests,
    int PendingConsents,
    int PendingDeletions);
