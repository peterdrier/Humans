using Humans.Application.DTOs.Governance;

namespace Humans.Application.DTOs;

/// <summary>
/// Shape returned by <c>IOnboardingService.GetBoardVotingDashboardAsync</c>.
/// Holds a stitched list of application rows (applicant display/picture
/// resolved via IUserService after the cross-domain nav was stripped) plus
/// the set of current Board members the view renders columns for.
/// </summary>
public record BoardVotingDashboardData(
    List<BoardVotingDashboardRow> Applications,
    List<BoardMemberInfo> BoardMembers);

public record BoardMemberInfo(Guid UserId, string DisplayName);
