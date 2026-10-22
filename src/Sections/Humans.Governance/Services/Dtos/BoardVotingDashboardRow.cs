using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record BoardVotingDashboardRow(
    Guid ApplicationId,
    Guid UserId,
    MembershipTier MembershipTier,
    string ApplicationMotivation,
    Instant SubmittedAt,
    ApplicationStatus Status,
    IReadOnlyList<BoardVoteRow> Votes);
