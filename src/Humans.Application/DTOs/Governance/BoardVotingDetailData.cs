using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Governance;

/// <summary>
/// Detail projection for the Board Voting detail view
/// (<c>BoardVotingDetail.cshtml</c>). Replaces the old <c>Application</c>
/// return from <c>OnboardingService.GetBoardVotingDetailAsync</c> whose
/// <c>.User.Profile</c> and <c>.BoardVotes[].BoardMemberUser</c> chains have
/// been stitched via <c>IUserService</c> + <c>IProfileService</c>.
/// </summary>
public record BoardVotingDetailData(
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
