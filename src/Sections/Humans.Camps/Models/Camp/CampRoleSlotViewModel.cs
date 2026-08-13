namespace Humans.Camps.Models;

internal sealed record CampRoleSlotViewModel(
    Guid AssignmentId, Guid CampMemberId, Guid UserId, string DisplayName);
