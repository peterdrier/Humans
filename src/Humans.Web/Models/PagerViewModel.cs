namespace Humans.Web.Models;

public sealed class PagerViewModel
{
    public required int TotalPages { get; init; }
    public required int CurrentPage { get; init; }
    public required string Action { get; init; }
    public string? Controller { get; init; }
    public int Window { get; init; } = 2;
}
