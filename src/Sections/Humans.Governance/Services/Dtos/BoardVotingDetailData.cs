using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record BoardVotingDetailData(
    Guid ApplicationId,
    Guid UserId,
    string FirstName,
    string LastName,
    MembershipTier MembershipTier,
    ApplicationStatus Status,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding,
    Instant SubmittedAt,
    IReadOnlyList<BoardVoteRow> Votes);
