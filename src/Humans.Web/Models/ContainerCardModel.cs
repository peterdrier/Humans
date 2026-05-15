namespace Humans.Web.Models;

public class ContainerCardModel
{
    public ContainerViewModel Container { get; set; } = null!;
    public ContainerPlacementViewModel? Placement { get; set; }
    public bool CanManage { get; set; }
    public string FormController { get; set; } = string.Empty;
    public string EditAction { get; set; } = string.Empty;
    public string DeleteAction { get; set; } = string.Empty;
    public Dictionary<string, string> ExtraRouteValues { get; set; } = new(StringComparer.Ordinal);

    public bool IsPlaced => Placement?.IsPlaced ?? false;
    public bool HasPlacementInfo => Placement?.HasPlacementInfo ?? false;
}
