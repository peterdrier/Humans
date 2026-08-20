namespace Humans.Users.Models;

internal static class AdminHumanListViewModelBuilder
{
    private const int PageSize = 20;

    public static AdminHumanListViewModel Build(
        IReadOnlyList<AdminHumanRow> rows,
        string? search,
        string? filter,
        string? sort,
        string? direction,
        int page)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var normalizedSort = sort?.ToLowerInvariant() ?? "name";
        var ascending = !string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        var pageNumber = Math.Max(page, 1);

        var humans = Sort(rows, normalizedSort, ascending)
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        return new AdminHumanListViewModel
        {
            Humans = humans,
            SearchTerm = search,
            StatusFilter = filter,
            SortBy = normalizedSort,
            SortDir = ascending ? "asc" : "desc",
            TotalCount = rows.Count,
            PageNumber = pageNumber,
            PageSize = PageSize
        };
    }

    private static IOrderedEnumerable<AdminHumanRow> Sort(
        IReadOnlyList<AdminHumanRow> rows,
        string sort,
        bool ascending) =>
        sort switch
        {
            "joined" => ascending
                ? rows.OrderBy(r => r.CreatedAt)
                : rows.OrderByDescending(r => r.CreatedAt),
            "login" => ascending
                ? rows.OrderBy(r => r.LastLoginAt.HasValue ? 0 : 1).ThenBy(r => r.LastLoginAt)
                : rows.OrderBy(r => r.LastLoginAt.HasValue ? 0 : 1).ThenByDescending(r => r.LastLoginAt),
            "status" => ascending
                ? rows.OrderBy(r => r.StatusLabel, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.StatusLabel, StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
}
