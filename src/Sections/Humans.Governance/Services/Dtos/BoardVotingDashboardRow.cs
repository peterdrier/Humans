using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record BoardVotingDashboardRow(
    Guid ApplicationId,
    Guid UserId,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    IReadOnlyList<BoardVoteRow> Votes);
