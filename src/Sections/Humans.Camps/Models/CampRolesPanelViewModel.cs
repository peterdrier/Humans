namespace Humans.Camps.Models;

internal sealed class CampRolesPanelViewModel
{
    public required IReadOnlyList<CampRoleRowViewModel> Rows { get; init; }
    public required bool CanManage { get; init; }
}
