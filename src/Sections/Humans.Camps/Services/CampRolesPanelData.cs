
namespace Humans.Camps.Services;

internal sealed record CampRolesPanelData(
    Guid CampSeasonId,
    IReadOnlyList<CampRolesPanelRow> Rows);

internal sealed record CampRolesPanelRow(
    CampRoleDefinition Definition,
    IReadOnlyList<CampRolesPanelSlot> FilledSlots,
    int EmptySlotCount,
    bool OverCapacity,
    int CurrentCount);

internal sealed record CampRolesPanelSlot(
    Guid AssignmentId,
    Guid CampMemberId,
    Guid UserId);
