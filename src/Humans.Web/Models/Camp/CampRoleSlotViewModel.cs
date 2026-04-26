namespace Humans.Web.Models.Camp;

public sealed record CampRoleSlotViewModel(
    Guid AssignmentId, Guid CampMemberId, Guid UserId, string DisplayName);
