namespace Humans.Containers.Contracts;

public class ContainerCardModel
{
    public ContainerDto Container { get; set; } = null!;
    public ContainerPlacementDto? Placement { get; set; }
    public string FormController { get; set; } = string.Empty;
    public string EditAction { get; set; } = string.Empty;
    public string DeleteAction { get; set; } = string.Empty;
    public Dictionary<string, string> ExtraRouteValues { get; set; } = new(StringComparer.Ordinal);

    public bool IsPlaced => Placement?.IsPlaced ?? false;
    public bool HasPlacementInfo => Placement?.HasPlacementInfo ?? false;
}
