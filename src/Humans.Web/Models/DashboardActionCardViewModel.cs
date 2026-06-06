namespace Humans.Web.Models;

public sealed class DashboardActionCardViewModel
{
    public string? Controller { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconCssClass { get; init; } = string.Empty;
    public string ColumnClass { get; init; } = "col-md-4";
    public string IconVariant { get; init; } = "neutral";
    public bool Compact { get; init; }
    public bool IsPost { get; init; }
    public string? ConfirmMessage { get; init; }
}
