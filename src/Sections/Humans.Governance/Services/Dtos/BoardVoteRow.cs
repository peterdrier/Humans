using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Projection of a single Board vote with the voter's display name stitched
/// from <c>IUserService</c>. Used by <c>OnboardingService</c>'s BoardVoting
/// methods after the <c>BoardVote.BoardMemberUser</c> cross-domain nav was
/// stripped.
/// </summary>
internal sealed record BoardVoteRow(
    Guid BoardMemberUserId,
    string? BoardMemberDisplayName,
    VoteChoice Vote,
    string? Note,
    Instant VotedAt);
