using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.DTOs;

public record BoardVotingDashboardData(
    List<MemberApplication> Applications,
    List<BoardMemberInfo> BoardMembers);

public record BoardMemberInfo(Guid UserId, string DisplayName);
