using Humans.Domain.Entities;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.DTOs;

public record AdminHumanDetailData(
    User User,
    Profile? Profile,
    IReadOnlyList<MemberApplication> Applications,
    int ConsentCount,
    IReadOnlyList<RoleAssignment> RoleAssignments,
    string? RejectedByName);
