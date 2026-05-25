namespace Humans.Web.Models;

public sealed class PagerViewModel(int totalPages, int currentPage, string action)
{
    public int TotalPages { get; } = totalPages;
    public int CurrentPage { get; } = currentPage;
    public string Action { get; } = action;
    public int Window { get; init; } = 2;
    public string NavCssClass { get; init; } = "";
    public string PaginationCssClass { get; init; } = "justify-content-center";
}
