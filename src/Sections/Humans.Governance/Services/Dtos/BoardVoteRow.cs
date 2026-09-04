using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Projection of a single Board vote with the voter's display name stitched
/// at the service layer — <c>BoardVote</c> carries no cross-domain nav.
/// </summary>
internal sealed record BoardVoteRow(
    Guid BoardMemberUserId,
    string? BoardMemberDisplayName,
    VoteChoice Vote,
    string? Note,
    Instant VotedAt);
