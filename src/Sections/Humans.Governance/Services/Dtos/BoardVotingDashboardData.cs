namespace Humans.Governance.Services.Dtos;

internal sealed record BoardVotingDashboardData(
    List<BoardVotingDashboardRow> Applications,
    List<BoardMemberInfo> BoardMembers);

internal sealed record BoardMemberInfo(Guid UserId, string DisplayName);
