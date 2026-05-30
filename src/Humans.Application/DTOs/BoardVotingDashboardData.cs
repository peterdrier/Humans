using Humans.Application.DTOs.Governance;

namespace Humans.Application.DTOs;

/// <summary>
/// Shape returned by <c>IApplicationDecisionService.GetBoardVotingDashboardAsync</c>.
/// Holds the list of application rows (identified by UserId; the view resolves
/// applicant display/picture itself via the human view component) plus the set
/// of current Board members the view renders columns for.
/// </summary>
public record BoardVotingDashboardData(
    List<BoardVotingDashboardRow> Applications,
    List<BoardMemberInfo> BoardMembers);

public record BoardMemberInfo(Guid UserId, string DisplayName);
