using Humans.Governance.Contracts;
using Humans.Governance.Services.Dtos;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Shape returned by <c>IApplicationDecisionService.GetBoardVotingDashboardAsync</c>.
/// Holds the list of application rows (identified by UserId; the view resolves
/// applicant display/picture itself via the human view component) plus the set
/// of current Board members the view renders columns for.
/// </summary>
internal sealed record BoardVotingDashboardData(
    List<BoardVotingDashboardRow> Applications,
    List<BoardMemberInfo> BoardMembers);

internal sealed record BoardMemberInfo(Guid UserId, string DisplayName);
