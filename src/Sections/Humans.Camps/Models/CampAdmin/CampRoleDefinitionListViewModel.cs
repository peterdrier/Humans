namespace Humans.Camps.Models;

internal sealed class CampRoleDefinitionListViewModel
{
    public required IReadOnlyList<CampRoleDefinitionListRowViewModel> Active { get; init; }
    public required IReadOnlyList<CampRoleDefinitionListRowViewModel> Deactivated { get; init; }
    public required int PublicYear { get; init; }
}

internal sealed record CampRoleDefinitionListRowViewModel(
    Guid Id, string Name, string Slug, string? Description, int SlotCount, int MinimumRequired,
    int SortOrder, bool IsActive, string? GroupEmail);
