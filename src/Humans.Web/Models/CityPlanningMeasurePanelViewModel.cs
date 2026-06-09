namespace Humans.Web.Models;

public sealed class CityPlanningMeasurePanelViewModel
{
    public string AccessMatrixSection { get; init; } = string.Empty;
    public string? AdminAction { get; init; }
    public string? AdminLabel { get; init; }
    public string AdminIconCssClass { get; init; } = "fa-solid fa-cog";
}
