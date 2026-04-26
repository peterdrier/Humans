using Humans.Domain.Entities;

namespace Humans.Application.Services.Camps;

/// <summary>
/// Service-layer DTO returned by <see cref="ICampRoleService.BuildPanelAsync"/>.
/// Web-layer view-models are mapped from this in the controller.
/// </summary>
public sealed record CampRolesPanelData(
    Guid CampSeasonId,
    IReadOnlyList<CampRolesPanelRow> Rows);

public sealed record CampRolesPanelRow(
    CampRoleDefinition Definition,
    IReadOnlyList<CampRolesPanelSlot> FilledSlots,
    int EmptySlotCount,
    bool OverCapacity,
    int CurrentCount);

public sealed record CampRolesPanelSlot(
    Guid AssignmentId,
    Guid CampMemberId,
    Guid UserId,
    string DisplayName);
