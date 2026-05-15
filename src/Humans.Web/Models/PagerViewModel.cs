namespace Humans.Web.Models;

public sealed class PagerViewModel
{
    public required int TotalPages { get; init; }
    public required int CurrentPage { get; init; }
    public required string Action { get; init; }
    public int Window { get; init; } = 2;
    public string NavCssClass { get; init; } = "";
    public string PaginationCssClass { get; init; } = "";
}
