namespace Humans.Web.Models.Camp;

public sealed class CampRoleRowViewModel
{
    public required Guid DefinitionId { get; init; }
    public required string Name { get; init; }
    public required string? Description { get; init; }
    public required int SlotCount { get; init; }
    public required int MinimumRequired { get; init; }
    public required IReadOnlyList<CampRoleSlotViewModel> FilledSlots { get; init; }
    public required int EmptySlotCount { get; init; }
    public required bool OverCapacity { get; init; }
    public required int CurrentCount { get; init; }
}
