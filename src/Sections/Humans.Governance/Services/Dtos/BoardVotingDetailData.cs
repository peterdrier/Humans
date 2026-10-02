using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Detail projection for the Governance Board Voting detail view
/// (<c>Views/Governance/BoardVoting/Detail.cshtml</c>). Applicant metadata
/// is stitched at the service layer; voter identities remain IDs.
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
