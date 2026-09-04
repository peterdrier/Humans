namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Shape returned by <c>ApplicationDecisionService.GetBoardVotingDashboardAsync</c>,
/// which the board-voting controller injects by concrete type. Holds the list of application rows (identified by UserId; the view resolves
/// applicant display/picture itself via the human view component) plus the set
/// of current Board members the view renders columns for.
/// </summary>
internal sealed record BoardVotingDashboardData(
    List<BoardVotingDashboardRow> Applications,
    List<BoardMemberInfo> BoardMembers);

internal sealed record BoardMemberInfo(Guid UserId, string DisplayName);
