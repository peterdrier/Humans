using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record BoardVoteRow(
    Guid BoardMemberUserId,
    VoteChoice Vote,
    string? Note,
    Instant VotedAt);
