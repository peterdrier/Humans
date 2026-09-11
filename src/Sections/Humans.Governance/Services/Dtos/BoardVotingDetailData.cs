using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Detail projection for the Governance Board Voting detail view
/// (<c>Views/Governance/BoardVoting/Detail.cshtml</c>). Applicant and voter
/// display names are stitched at the service layer — no cross-domain navs.
/// </summary>
internal sealed record BoardVotingDetailData(
    Guid ApplicationId,
    Guid UserId,
    string DisplayName,
    string? ProfilePictureUrl,
    string Email,
    string FirstName,
    string LastName,
    string? City,
    string? CountryCode,
    MembershipTier MembershipTier,
    ApplicationStatus Status,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding,
    Instant SubmittedAt,
    IReadOnlyList<BoardVoteRow> Votes);
