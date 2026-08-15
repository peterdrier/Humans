
namespace Humans.Camps.Services;

/// <summary>
/// Service-layer DTO returned by <see cref="ICampRoleService.BuildPanelAsync"/>.
/// Web-layer view-models are mapped from this in the controller.
/// </summary>
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
    Guid UserId,
    string DisplayName);
